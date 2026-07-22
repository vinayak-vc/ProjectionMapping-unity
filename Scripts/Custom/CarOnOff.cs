using UnityEngine;

public class CarOnOff : MonoBehaviour {
    public GameObject car;
    public GameObject box;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space)) {
            car.SetActive(!car.activeSelf);
            box.SetActive(!box.activeSelf);
        }
    }
}
