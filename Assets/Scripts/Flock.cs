using UnityEngine;

public class Flock : MonoBehaviour
{
    FlockManager manager;
    float        speed;

    void Start()
    {
        manager = FlockManager.instance;
        // Each fish gets its own random speed so they arrive at different times
        speed = Random.Range(manager.minSpeed, manager.maxSpeed);
    }

    void Update()
    {
        Vector3 goal = manager.goalPosition;

        // Rotate smoothly toward the goal
        Vector3 dir = (goal - transform.position);
        if (dir != Vector3.zero)
        {
            Quaternion targetRot = Quaternion.LookRotation(dir.normalized);
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                targetRot,
                manager.rotationSpeed * Time.deltaTime
            );
        }

        // Move forward at individual speed
        transform.Translate(0, 0, speed * Time.deltaTime);
    }
}
