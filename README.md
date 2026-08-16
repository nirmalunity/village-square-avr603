# The Village Square

Unity 6 URP mini-game for **AVR603 — Artificial Intelligence for Games**
(Westcliff University, Summer 2026).

Defend the village from zombie waves while keeping the villagers alive.
Zombie combat strategy runs on a neural network trained with ML-Agents (PPO).

**Unity 6000.3.11f1** · URP 17.3.0 · ML-Agents 2.0.2 · AI Navigation 2.0.11

---

## Setup

Art assets ship separately as `VillageSquare-Assets.zip` (~2.6 GB) — four character
`.fbx` exceed GitHub's 100 MB file limit, and the Asset Store packs aren't
redistributable.

```bash
git clone <repository-url>
cd NPC-MiniGame

# Extract the five art folders into Assets/
unzip /path/to/VillageSquare-Assets.zip -d Assets/
```

PowerShell:

```powershell
Expand-Archive -Path "C:\path\to\VillageSquare-Assets.zip" -DestinationPath ".\Assets\"
```

Verify — all five must resolve:

```bash
ls -d Assets/Characters Assets/MyDreamGameStudio Assets/Silver_Cats \
      "Assets/Polytope Studio" Assets/Packages
```

> ⚠️ The zip's `Packages` folder belongs in **`Assets/`**, not the project root.
> Root `Packages/` holds the package manifest and comes from git — don't overwrite it.

Then:

1. Open in Unity 6000.3.11f1 (first import takes several minutes)
2. Load `Assets/Scenes/VillageSquare.unity`
3. Bake the NavMesh — `Window ▸ AI ▸ Navigation ▸ Bake`

Pink materials after import → reimport the affected folder.

---

## Play

`W`/`S` move · mouse look · left click attack · `R` restart

Approach the Elder to start. Win by surviving the 2-minute timer, or clearing all
waves and returning to the Elder. Lose if the hero dies or all villagers are killed.

---

## AI systems

| Script | Technique |
|---|---|
| `ZombieAgent.cs` | ML-Agents PPO — 11 observations, 4 discrete actions |
| `NPCNavMesh.cs` | FSM + utility AI + NavMesh pathfinding |
| `ZombieSwarmManager.cs` | Encirclement slots, alert propagation |
| `ZombieQLearner.cs` | Tabular Q-learning (fallback when no model assigned) |
| `VillagerBoid.cs` | Boids flocking |
| `ZombieWaveSpawner.cs` | Escalating waves with live-zombie cap |

`Assets/ML/ZombieBrain.onnx` is trained and already assigned to the zombie prefab
— no Python needed to play.

---

## Retraining

```bash
conda create -n mlagents-p310 python=3.10 -y
conda activate mlagents-p310
pip install mlagents==1.1.0

mlagents-learn Assets/ML/training_config.yaml --run-id=<id>
```

Before pressing Play:

1. Zombie prefab → BehaviorParameters: clear **Model**, **Behavior Type** = `Default`
2. GameManager → tick **Training Mode**
3. Press Play when the terminal reports listening on port 5004

After training:

```bash
cp results/<id>/ZombieBrain.onnx Assets/ML/
```

Reassign the model on the prefab, set **Behavior Type** = `Inference Only`,
untick **Training Mode**.

`TrainingPlayerBot` drives the hero during training, varying its hit accuracy so
the model sees both skilled and unskilled opponents.
