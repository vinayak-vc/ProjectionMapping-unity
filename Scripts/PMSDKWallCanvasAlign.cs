using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using vxpmsdk.Components;

namespace ProjectionMappingSample {
    // On-site two-projector wall alignment. StartAutoAlign(all) only recovers each
    // projector's current geometry; for a shared wall the projectors must be mapped
    // onto ONE canvas so the split-slice content lines up in the overlap. This
    // component sweeps each projector with the OTHER one blanked (a lit second
    // projector showing animated content corrupts the Gray-code white/black
    // references), then fits camera->projector homographies itself with RANSAC:
    // ultra-short-throw projectors spill decodable light onto the floor and
    // ceiling, and those correspondences lie on different planes than the wall —
    // a plain least-squares fit (as used inside PMSDKAutoAlign) is poisoned by
    // them. The consensus fit keeps only wall-plane points. Pins are then set
    // directly from the filtered fit — no second sweep needed.
    public class PMSDKWallCanvasAlign : MonoBehaviour {
        public int WebcamIndex = 0;
        public int WebcamFlushFrames = 5;
        public int SettleFrames = 45;
        [Tooltip("RANSAC inlier tolerance in projector-normalized units (0.02 = ~2.5px on a 128 raster).")]
        public float InlierTolerance = 0.02f;
        public int RansacIterations = 300;
        public int MinInliers = 150;
        [Tooltip("Runtime hotkey that starts the wall-canvas alignment (None = disabled). Point the observer webcam at the wall so it sees BOTH projections fully, darken the room, then press it.")]
        public KeyCode RunKey = KeyCode.F4;
        [Tooltip("Optional file that receives one status line per step (for external monitoring). Empty = off.")]
        public string StatusFilePath = "";
        public string Status = "idle";
        public bool IsRunning { get; private set; }

        private void Update() {
            if (RunKey != KeyCode.None && Input.GetKeyDown(RunKey) && !IsRunning) {
                Begin();
            }
        }

        private class RobustFit {
            public PMSDKHomography CamToProj;
            public PMSDKHomography ProjToCam;
            public int Inliers;
            public int Total;
            public float RmsProjPx;
        }

        public void Begin() {
            if (IsRunning) {
                return;
            }
            StartCoroutine(Run());
        }

        private void Report(string message) {
            Status = message;
            Debug.Log("[PMSDKWallCanvasAlign] " + message);
            if (!string.IsNullOrEmpty(StatusFilePath)) {
                try {
                    System.IO.File.AppendAllText(StatusFilePath, message + "\n");
                } catch (System.Exception) {
                    // Status file is best-effort only; never fail the sweep over it.
                }
            }
        }

        private IEnumerator Run() {
            IsRunning = true;
            PMSDKCalibrationManager manager = Object.FindFirstObjectByType<PMSDKCalibrationManager>(FindObjectsInactive.Include);
            if (manager == null) {
                Report("END: no PMSDKCalibrationManager in scene");
                IsRunning = false;
                yield break;
            }
            IReadOnlyList<PMSDKCalibrationManager.Surface> surfaces = manager.Surfaces;
            if (surfaces.Count != 2) {
                Report("END: expected 2 surfaces, found " + surfaces.Count);
                IsRunning = false;
                yield break;
            }

            PMSDKCalibrationManager.Surface left = null;
            PMSDKCalibrationManager.Surface right = null;
            foreach (PMSDKCalibrationManager.Surface s in surfaces) {
                if (s.Id.ToLower().Contains("left")) {
                    left = s;
                } else {
                    right = s;
                }
            }
            if (left == null || right == null) {
                Report("END: could not identify Left/Right surfaces by name");
                IsRunning = false;
                yield break;
            }

            MeshRenderer leftRenderer = left.Warp.GetComponent<MeshRenderer>();
            MeshRenderer rightRenderer = right.Warp.GetComponent<MeshRenderer>();
            float slice = leftRenderer.sharedMaterial.mainTextureScale.x;
            float rightOffset = rightRenderer.sharedMaterial.mainTextureOffset.x;
            float blendFraction = (slice - rightOffset) / slice;
            Report("slices: left=[0," + slice.ToString("F3") + "] right=[" + rightOffset.ToString("F3") + ",1] blendFraction=" + blendFraction.ToString("F3"));

            // Freeze animated content for the whole procedure.
            List<Rigidbody> frozen = new List<Rigidbody>();
            foreach (Rigidbody body in Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None)) {
                if (!body.isKinematic) {
                    body.isKinematic = true;
                    frozen.Add(body);
                }
            }
            SetOverlaysVisible(surfaces, false);

            PMSDKAutoAlign aligner = gameObject.GetComponent<PMSDKAutoAlign>();
            if (aligner == null) {
                aligner = gameObject.AddComponent<PMSDKAutoAlign>();
            }
            aligner.SettleFrames = SettleFrames;

            ResetPin(left.CornerPin);
            ResetPin(right.CornerPin);

            // --- one sweep per projector (identity pins) ---
            PMSDKAutoAlign.Result leftObserved = new PMSDKAutoAlign.Result();
            PMSDKAutoAlign.Result rightObserved = new PMSDKAutoAlign.Result();
            Report("Sweeping Left_Screen...");
            yield return Sweep(aligner, left, right, null, delegate (PMSDKAutoAlign.Result r) { leftObserved = r; });
            Report("left sweep: " + leftObserved.Message);
            Report("Sweeping Right_Screen...");
            yield return Sweep(aligner, right, left, null, delegate (PMSDKAutoAlign.Result r) { rightObserved = r; });
            Report("right sweep: " + rightObserved.Message);

            // The sweep itself sets pins from its unfiltered internal fit; ignore
            // that — the robust fits below are authoritative.
            ResetPin(left.CornerPin);
            ResetPin(right.CornerPin);

            RobustFit leftFit = FitRobust(leftObserved);
            RobustFit rightFit = FitRobust(rightObserved);
            if (leftFit == null || rightFit == null) {
                Restore(frozen, surfaces);
                Report("END: robust fit failed (left=" + Describe(leftFit) + " right=" + Describe(rightFit) + ") — check camera view / room light");
                IsRunning = false;
                yield break;
            }
            Report("robust fit left: " + Describe(leftFit));
            Report("robust fit right: " + Describe(rightFit));

            Vector2 leftTL, leftTR, leftBR, leftBL, rightTL, rightTR, rightBR, rightBL;
            ObservedQuad(leftFit.ProjToCam, out leftTL, out leftTR, out leftBR, out leftBL);
            ObservedQuad(rightFit.ProjToCam, out rightTL, out rightTR, out rightBR, out rightBL);

            // Shared canvas = outer corners of the union (left's left edge, right's
            // right edge). Parametrized u right, v down in camera space.
            List<Vector2> unit = new List<Vector2> { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            List<Vector2> canvasCorners = new List<Vector2> { leftTL, rightTR, rightBR, leftBL };
            PMSDKHomography canvas = PMSDKHomography.Fit(unit, canvasCorners);
            if (!canvas.Valid) {
                Restore(frozen, surfaces);
                Report("END: canvas homography degenerate");
                IsRunning = false;
                yield break;
            }
            Report("canvas TL=" + leftTL.ToString("F3") + " TR=" + rightTR.ToString("F3") + " BR=" + rightBR.ToString("F3") + " BL=" + leftBL.ToString("F3"));

            // --- map each projector's raster onto its content slice of the canvas ---
            ApplyPin(left.CornerPin, leftFit.CamToProj, canvas, 0f, slice);
            ApplyPin(right.CornerPin, rightFit.CamToProj, canvas, rightOffset, 1f);
            Report("left pin: " + PinString(left.CornerPin));
            Report("right pin: " + PinString(right.CornerPin));

            left.Blend.LeftEdge = 0f;
            left.Blend.RightEdge = blendFraction;
            right.Blend.LeftEdge = blendFraction;
            right.Blend.RightEdge = 0f;
            left.Blend.enabled = true;
            right.Blend.enabled = true;
            manager.SaveNow();
            Report("blend widths applied (inner edges " + blendFraction.ToString("F3") + "), calibration saved");

            Restore(frozen, surfaces);
            Report("END: OK — wall canvas alignment complete");
            IsRunning = false;
        }

        private static string Describe(RobustFit fit) {
            if (fit == null) {
                return "FAILED";
            }
            return fit.Inliers + "/" + fit.Total + " inliers, rms " + fit.RmsProjPx.ToString("F2") + "px";
        }

        private static string PinString(PMSDKCornerPin pin) {
            return "TL=" + pin.TopLeft.ToString("F3") + " TR=" + pin.TopRight.ToString("F3") + " BR=" + pin.BottomRight.ToString("F3") + " BL=" + pin.BottomLeft.ToString("F3");
        }

        private static void ApplyPin(PMSDKCornerPin pin, PMSDKHomography camToProj, PMSDKHomography canvas, float u0, float u1) {
            Vector2 targetTL = canvas.Apply(new Vector2(u0, 0f));
            Vector2 targetTR = canvas.Apply(new Vector2(u1, 0f));
            Vector2 targetBR = canvas.Apply(new Vector2(u1, 1f));
            Vector2 targetBL = canvas.Apply(new Vector2(u0, 1f));
            pin.TopLeft = camToProj.Apply(targetTL);
            pin.TopRight = camToProj.Apply(targetTR);
            pin.BottomRight = camToProj.Apply(targetBR);
            pin.BottomLeft = camToProj.Apply(targetBL);
        }

        private IEnumerator Sweep(PMSDKAutoAlign aligner, PMSDKCalibrationManager.Surface target, PMSDKCalibrationManager.Surface other, Vector2[] targetQuad, System.Action<PMSDKAutoAlign.Result> onDone) {
            Camera otherCamera = other.ProjectorCamera;
            int savedMask = otherCamera.cullingMask;
            CameraClearFlags savedFlags = otherCamera.clearFlags;
            Color savedBackground = otherCamera.backgroundColor;
            otherCamera.cullingMask = 0;
            otherCamera.clearFlags = CameraClearFlags.SolidColor;
            otherCamera.backgroundColor = Color.black;

            PMSDKNativeWebcamCamera webcam = new PMSDKNativeWebcamCamera(WebcamIndex, WebcamFlushFrames);
            yield return aligner.AlignSurface(target, webcam, targetQuad, onDone);

            otherCamera.cullingMask = savedMask;
            otherCamera.clearFlags = savedFlags;
            otherCamera.backgroundColor = savedBackground;
        }

        private RobustFit FitRobust(PMSDKAutoAlign.Result result) {
            if (!result.Success || result.Correspondence == null || result.CamW <= 0 || result.CamH <= 0) {
                return null;
            }
            List<Vector2> camPts = new List<Vector2>();
            List<Vector2> projPts = new List<Vector2>();
            int step = Mathf.Max(1, Mathf.RoundToInt(Mathf.Sqrt((result.CamW * result.CamH) / 4000f)));
            for (int y = 0; y < result.CamH; y += step) {
                for (int x = 0; x < result.CamW; x += step) {
                    PMSDKGrayCodeDecode.Correspondence c = result.Correspondence[y * result.CamW + x];
                    if (!c.Valid) {
                        continue;
                    }
                    camPts.Add(new Vector2((x + 0.5f) / result.CamW, (y + 0.5f) / result.CamH));
                    projPts.Add(new Vector2((c.ProjectorX + 0.5f) / result.ProjW, (c.ProjectorY + 0.5f) / result.ProjH));
                }
            }
            int total = camPts.Count;
            if (total < MinInliers) {
                return null;
            }

            List<int> bestInliers = null;
            List<Vector2> sampleCam = new List<Vector2> { Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero };
            List<Vector2> sampleProj = new List<Vector2> { Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero };
            for (int iteration = 0; iteration < RansacIterations; iteration++) {
                int a = Random.Range(0, total);
                int b = Random.Range(0, total);
                int c = Random.Range(0, total);
                int d = Random.Range(0, total);
                if (a == b || a == c || a == d || b == c || b == d || c == d) {
                    continue;
                }
                sampleCam[0] = camPts[a]; sampleCam[1] = camPts[b]; sampleCam[2] = camPts[c]; sampleCam[3] = camPts[d];
                sampleProj[0] = projPts[a]; sampleProj[1] = projPts[b]; sampleProj[2] = projPts[c]; sampleProj[3] = projPts[d];
                PMSDKHomography candidate = PMSDKHomography.Fit(sampleCam, sampleProj);
                if (candidate == null || !candidate.Valid) {
                    continue;
                }
                List<int> inliers = new List<int>();
                for (int i = 0; i < total; i++) {
                    if (Vector2.Distance(candidate.Apply(camPts[i]), projPts[i]) < InlierTolerance) {
                        inliers.Add(i);
                    }
                }
                if (bestInliers == null || inliers.Count > bestInliers.Count) {
                    bestInliers = inliers;
                }
            }
            if (bestInliers == null || bestInliers.Count < MinInliers) {
                return null;
            }

            List<Vector2> inCam = new List<Vector2>(bestInliers.Count);
            List<Vector2> inProj = new List<Vector2>(bestInliers.Count);
            foreach (int index in bestInliers) {
                inCam.Add(camPts[index]);
                inProj.Add(projPts[index]);
            }
            PMSDKHomography camToProj = PMSDKHomography.Fit(inCam, inProj);
            PMSDKHomography projToCam = PMSDKHomography.Fit(inProj, inCam);
            if (camToProj == null || !camToProj.Valid || projToCam == null || !projToCam.Valid) {
                return null;
            }
            double sumSquared = 0.0;
            for (int i = 0; i < inCam.Count; i++) {
                float distance = Vector2.Distance(camToProj.Apply(inCam[i]), inProj[i]);
                sumSquared += distance * distance;
            }
            RobustFit fit = new RobustFit();
            fit.CamToProj = camToProj;
            fit.ProjToCam = projToCam;
            fit.Inliers = bestInliers.Count;
            fit.Total = total;
            fit.RmsProjPx = Mathf.Sqrt((float)(sumSquared / inCam.Count)) * result.ProjW;
            return fit;
        }

        private static void ObservedQuad(PMSDKHomography projToCam, out Vector2 tl, out Vector2 tr, out Vector2 br, out Vector2 bl) {
            Vector2[] corners = new Vector2[] {
                projToCam.Apply(new Vector2(0f, 0f)),
                projToCam.Apply(new Vector2(1f, 0f)),
                projToCam.Apply(new Vector2(1f, 1f)),
                projToCam.Apply(new Vector2(0f, 1f))
            };
            tl = PickCorner(corners, -1f, -1f);
            tr = PickCorner(corners, 1f, -1f);
            br = PickCorner(corners, 1f, 1f);
            bl = PickCorner(corners, -1f, 1f);
        }

        private static Vector2 PickCorner(Vector2[] corners, float signX, float signY) {
            Vector2 best = corners[0];
            float bestScore = float.NegativeInfinity;
            foreach (Vector2 corner in corners) {
                float score = corner.x * signX + corner.y * signY;
                if (score > bestScore) {
                    bestScore = score;
                    best = corner;
                }
            }
            return best;
        }

        private static void ResetPin(PMSDKCornerPin pin) {
            pin.TopLeft = new Vector2(0f, 1f);
            pin.TopRight = new Vector2(1f, 1f);
            pin.BottomLeft = new Vector2(0f, 0f);
            pin.BottomRight = new Vector2(1f, 0f);
        }

        private static void SetOverlaysVisible(IReadOnlyList<PMSDKCalibrationManager.Surface> surfaces, bool visible) {
            foreach (PMSDKCalibrationManager.Surface s in surfaces) {
                if (s.Overlay != null) {
                    s.Overlay.SetVisible(visible);
                }
            }
        }

        private void Restore(List<Rigidbody> frozen, IReadOnlyList<PMSDKCalibrationManager.Surface> surfaces) {
            foreach (Rigidbody body in frozen) {
                body.isKinematic = false;
            }
            SetOverlaysVisible(surfaces, true);
        }
    }
}
