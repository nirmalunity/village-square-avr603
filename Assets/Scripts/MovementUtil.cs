using UnityEngine;

public static class MovementUtil
{
    const int   SAMPLES    = 16;    // directions tested per sweep (22.5° apart)
    const float EYE_HEIGHT = 0.8f;  // raycast origin height (chest level)

    /// Sweeps all directions and returns the most open one.
    /// preferredDir — where the character would like to go (pass Vector3.zero if no preference).
    /// probeDist    — how far ahead to check for obstacles.
    /// boundarySize — half-extent of the play area; used to steer away from map edges.
    public static Vector3 FindClearDirection(Vector3 position,
                                             Vector3 preferredDir,
                                             float   probeDist    = 5f,
                                             float   boundarySize = 0f)
    {
        Vector3 origin = position + Vector3.up * EYE_HEIGHT;

        preferredDir.y = 0f;
        bool hasPreference = preferredDir.sqrMagnitude > 0.01f;
        if (hasPreference) preferredDir.Normalize();

        Vector3 best      = Vector3.forward;
        float   bestScore = float.MinValue;

        for (int i = 0; i < SAMPLES; i++)
        {
            float   angle = i * (360f / SAMPLES);
            Vector3 dir   = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;

            // ── 1. Clearance: how far can we travel this way? ─────────────────
            float clearance = probeDist;
            if (Physics.Raycast(origin, dir, out RaycastHit hit, probeDist,
                                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                // Other characters are pushable, not walls — don't treat them as blockers
                bool isCharacter = hit.collider.GetComponent<CharacterController>() != null
                                || hit.collider.GetComponent<VillagerBoid>()        != null
                                || hit.collider.GetComponent<NPCNavMesh>()          != null;
                clearance = isCharacter ? probeDist : hit.distance;
            }

            float score = (clearance / probeDist) * 2f;   // open space matters most

            // ── 2. Alignment with where we want to go ─────────────────────────
            if (hasPreference)
                score += Vector3.Dot(dir, preferredDir);  // -1 .. +1

            // ── 3. Penalty for heading toward the map edge ────────────────────
            if (boundarySize > 0f)
            {
                Vector3 ahead = position + dir * probeDist;
                float   overX = Mathf.Abs(ahead.x) - boundarySize;
                float   overZ = Mathf.Abs(ahead.z) - boundarySize;
                if (overX > 0f || overZ > 0f)
                    score -= 3f;   // strong penalty — this is what causes corner traps
            }

            if (score > bestScore) { bestScore = score; best = dir; }
        }

        return best;
    }

    /// Convenience: direction that moves away from a set of threats while still
    /// preferring open space. Used when fleeing a group rather than one target.
    public static Vector3 FleeDirection(Vector3 position,
                                        Vector3 threatCentre,
                                        float   probeDist    = 6f,
                                        float   boundarySize = 0f)
    {
        Vector3 away = position - threatCentre;
        away.y = 0f;
        if (away.sqrMagnitude < 0.01f) away = Random.insideUnitSphere;
        away.y = 0f;

        // Ask for the clearest direction that still points roughly away from the threat
        return FindClearDirection(position, away.normalized, probeDist, boundarySize);
    }
}
