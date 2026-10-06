using UnityEngine;

// Finish line for a course: when the player walks into the volume, flash a message
// with the time since they last spawned. Once per run (a respawn starts a new run).
[RequireComponent(typeof(BoxCollider))]
public class GoalZone : MonoBehaviour
{
    public string message = "Reached Tower B";

    BoxCollider zone;
    FirstPersonController player;
    float reachedRun = float.NaN;
    string shown; float shownUntil;

    void Start()
    {
        zone = GetComponent<BoxCollider>();
        zone.isTrigger = true;
        player = FindAnyObjectByType<FirstPersonController>(FindObjectsInactive.Include);
    }

    void Update()
    {
        if (player == null || !player.isActiveAndEnabled) return;
        if (!zone.bounds.Contains(player.transform.position + Vector3.up)) return;
        if (reachedRun == player.SpawnTime) return;
        reachedRun = player.SpawnTime;
        shown = $"{message}: {Time.time - player.SpawnTime:0.0} s";
        shownUntil = Time.time + 6f;
    }

    void OnGUI()
    {
        if (Time.time < shownUntil) GUI.Label(new Rect(Screen.width / 2f - 150, 100, 400, 25), shown);
    }
}
