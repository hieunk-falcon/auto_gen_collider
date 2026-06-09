using UnityEngine;

public class TriggerTest : MonoBehaviour
{
    private int count;

    private void OnTriggerEnter(Collider other)
    {
        count++;
        Debug.Log($"Trigger Enter {count}");
    }
}