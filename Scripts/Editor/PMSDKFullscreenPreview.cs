using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

using UnityEditor;

using UnityEngine;

namespace ProjectionMappingSample {
    // Opens a borderless Game view on each non-primary monitor so the physical
    // projectors receive fullscreen output straight from the editor (no build).
    // Monitors sorted left-to-right receive Display 2, Display 3, ... while the
    // operator console (calibration HUD, Display 1) stays on the primary monitor.
    public static class PMSDKFullscreenPreview {
        private const string WindowMarker = "PMSDK_Fullscreen_Preview";

        private static bool ArePreviewWindowsOpen() {
            Type gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");

            UnityEngine.Object[] windows = Resources.FindObjectsOfTypeAll(gameViewType);

            foreach (UnityEngine.Object obj in windows) {
                EditorWindow window = obj as EditorWindow;

                if (window != null && window.name == WindowMarker) {
                    return true;
                }
            }

            return false;
        }

        [MenuItem("Tools/Projection Mapping/Fullscreen Previews/Toggle %#-")]
        public static void Toggle() {
            if (ArePreviewWindowsOpen()) {
                CloseExisting();
            } else {
                Open();
            }
        }

        [MenuItem("Tools/Projection Mapping/Fullscreen Previews/Open")]
        public static void Open() {
            CloseExisting();
            List<MonitorRect> monitors = GetMonitors();
            monitors.Sort(CompareByLeft);
            float pixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
            Type gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            PropertyInfo targetDisplay = FindProperty(gameViewType, "targetDisplay");
            // showToolbar is declared on the base PlayModeView, not GameView, so it must be
            // resolved by walking the type hierarchy (a NonPublic lookup on the derived type
            // alone returns null — which is why the strip used to stay visible). It must also be
            // set AFTER ShowPopup and re-applied next tick, because the GameView rebuilds (and
            // re-shows) its toolbar on its first OnGUI.
            PropertyInfo showToolbar = FindProperty(gameViewType, "showToolbar");
            int displayIndex = 1; // Camera targetDisplay 1 == "Display 2"
            foreach (MonitorRect monitor in monitors) {
                if (monitor.isPrimary) {
                    continue;
                }
                OpenWindow(gameViewType, targetDisplay, showToolbar, monitor, displayIndex, pixelsPerPoint);
                displayIndex++;
            }
            if (showToolbar == null) {
                Debug.LogWarning("PMSDKFullscreenPreview: could not resolve GameView.showToolbar on this Unity version; the toolbar strip may still show.");
            }
            if (displayIndex == 1) {
                Debug.LogWarning("PMSDKFullscreenPreview: no secondary monitors found. Connect the projectors as extended displays first.");
            } else {
                Debug.Log("PMSDKFullscreenPreview: opened " + (displayIndex - 1) + " fullscreen preview(s). Close via Tools > Projection Mapping > Fullscreen Previews > Close.");
            }
        }

        // For a projector that duplicates the primary monitor: one borderless Game view showing
        // Display 1 over the whole primary monitor, so the projected raster has no editor strips.
        [MenuItem("Tools/Projection Mapping/Fullscreen Previews/Open On Primary")]
        public static void OpenOnPrimary() {
            CloseExisting();
            List<MonitorRect> monitors = GetMonitors();
            Type gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            PropertyInfo targetDisplay = FindProperty(gameViewType, "targetDisplay");
            PropertyInfo showToolbar = FindProperty(gameViewType, "showToolbar");
            foreach (MonitorRect monitor in monitors) {
                if (!monitor.isPrimary) {
                    continue;
                }
                OpenWindow(gameViewType, targetDisplay, showToolbar, monitor, 0, EditorGUIUtility.pixelsPerPoint);
                // On the primary monitor other apps can cover the preview (and the projector would show them):
                // keep it on top once the native window exists.
                MonitorRect captured = monitor;
                EditorApplication.delayCall += () => KeepOnTop(captured);
                Debug.Log("PMSDKFullscreenPreview: opened a fullscreen preview of Display 1 on the primary monitor. Close via Tools > Projection Mapping > Fullscreen Previews > Close.");
                return;
            }
            Debug.LogWarning("PMSDKFullscreenPreview: no primary monitor found.");
        }

        private static void OpenWindow(Type gameViewType, PropertyInfo targetDisplay, PropertyInfo showToolbar, MonitorRect monitor,
            int displayIndex, float pixelsPerPoint) {
            EditorWindow window = (EditorWindow)ScriptableObject.CreateInstance(gameViewType);
            window.name = WindowMarker;
            if (targetDisplay != null) {
                targetDisplay.SetValue(window, displayIndex, null);
            }
            Rect rect = new Rect(
                monitor.x / pixelsPerPoint,
                monitor.y / pixelsPerPoint,
                monitor.width / pixelsPerPoint,
                monitor.height / pixelsPerPoint);
            window.ShowPopup();
            window.minSize = new Vector2(rect.width, rect.height);
            window.maxSize = window.minSize;
            window.position = rect;
            HideToolbar(window, showToolbar);
            // Re-apply after the GameView's first OnGUI, which otherwise restores the strip.
            EditorWindow captured = window;
            PropertyInfo capturedProp = showToolbar;
            EditorApplication.delayCall += () => HideToolbar(captured, capturedProp);
        }

        [MenuItem("Tools/Projection Mapping/Fullscreen Previews/Close")]
        public static void CloseExisting() {
            Type gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            UnityEngine.Object[] windows = Resources.FindObjectsOfTypeAll(gameViewType);
            foreach (UnityEngine.Object candidate in windows) {
                EditorWindow window = candidate as EditorWindow;
                if (window != null && window.name == WindowMarker) {
                    window.Close();
                }
            }
        }

        // Resolve a property by name anywhere up the type hierarchy (GameView -> PlayModeView -> ...),
        // including non-public members declared on base classes (a plain GetProperty on the derived
        // type does not return those).
        private static PropertyInfo FindProperty(Type type, string name) {
            for (Type current = type; current != null; current = current.BaseType) {
                PropertyInfo prop = current.GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (prop != null) {
                    return prop;
                }
            }
            return null;
        }

        private static void HideToolbar(EditorWindow window, PropertyInfo showToolbar) {
            if (window == null || showToolbar == null || !showToolbar.CanWrite) {
                return;
            }
            showToolbar.SetValue(window, false, null);
            window.Repaint();
        }

        private struct MonitorRect {
            public float x;
            public float y;
            public float width;
            public float height;
            public bool isPrimary;
        }

        private static int CompareByLeft(MonitorRect a, MonitorRect b) {
            return a.x.CompareTo(b.x);
        }

#if UNITY_EDITOR_WIN
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo {
            public uint size;
            public NativeRect monitor;
            public NativeRect work;
            public uint flags;
        }

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref NativeRect rect, IntPtr data);

        private delegate bool WindowEnumProc(IntPtr window, IntPtr data);

        private static readonly IntPtr TopMost = new IntPtr(-1);
        private const uint NoSize = 0x0001;
        private const uint NoMove = 0x0002;
        private const uint NoActivate = 0x0010;

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(WindowEnumProc callback, IntPtr data);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        // The popup is the visible window of this process that covers exactly the monitor.
        private static void KeepOnTop(MonitorRect monitor) {
            uint ownProcess = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            int found = 0;
            WindowEnumProc callback = delegate (IntPtr window, IntPtr data) {
                uint process;
                GetWindowThreadProcessId(window, out process);
                NativeRect rect;
                if (process == ownProcess && IsWindowVisible(window) && GetWindowRect(window, out rect) &&
                    rect.left == (int)monitor.x && rect.top == (int)monitor.y &&
                    rect.right - rect.left == (int)monitor.width && rect.bottom - rect.top == (int)monitor.height) {
                    SetWindowPos(window, TopMost, 0, 0, 0, 0, NoMove | NoSize | NoActivate);
                    found++;
                }
                return true;
            };
            EnumWindows(callback, IntPtr.Zero);
            if (found == 0) {
                Debug.LogWarning("PMSDKFullscreenPreview: could not find the preview window to keep it on top; other windows may cover the projection.");
            }
        }

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        private static List<MonitorRect> GetMonitors() {
            List<MonitorRect> result = new List<MonitorRect>();
            MonitorEnumProc callback = delegate (IntPtr monitor, IntPtr hdc, ref NativeRect rect, IntPtr data) {
                MonitorInfo info = new MonitorInfo();
                info.size = (uint)Marshal.SizeOf(typeof(MonitorInfo));
                if (GetMonitorInfo(monitor, ref info)) {
                    MonitorRect entry = new MonitorRect();
                    entry.x = info.monitor.left;
                    entry.y = info.monitor.top;
                    entry.width = info.monitor.right - info.monitor.left;
                    entry.height = info.monitor.bottom - info.monitor.top;
                    entry.isPrimary = (info.flags & 1u) != 0u;
                    result.Add(entry);
                }
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            return result;
        }
#else
        private static List<MonitorRect> GetMonitors() {
            return new List<MonitorRect>();
        }

        private static void KeepOnTop(MonitorRect monitor) {
        }
#endif
    }
}