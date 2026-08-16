"""
Village Square — Zombie Q-Table Pre-Trainer
============================================
Trains a Q-table offline by simulating thousands of combat episodes.
The resulting table is loaded by ZombieQLearner.cs in Unity, giving
zombies a sensible starting strategy instead of random behaviour.

State space (18 states = 3 x 3 x 2):
  player_hp_bucket   : 0=low(<33%)   1=mid(33-66%)  2=high(>66%)
  distance_bucket    : 0=close(<3.5) 1=medium(<9)   2=far(>=9)
  hit_pressure_bucket: 0=low(<0.35)  1=high(>=0.35)

Actions (4):
  0 = Chase          — charge the player directly
  1 = Flank          — arc around and attack from the side
  2 = RetreatVillager— disengage, go attack a villager instead
  3 = GroupUp        — move toward the nearest ally zombie

Rewards mirror the Unity implementation exactly:
  Hit player         : +1.0
  Take a hit         : -0.4
  Die                : -2.0
  Hit villager       : +0.5
  Survive per step   : +0.02

Run:  python train_qtable.py
Output: qtable_pretrained.txt  (copy to Assets/StreamingAssets/)
"""

import numpy as np
import random
import os

# ── Hyperparameters (match ZombieQLearner.cs) ─────────────────────────────────
ALPHA        = 0.6     # learning rate
GAMMA        = 0.9     # discount factor — more farsighted so multi-step chase strategy is valued
EPSILON_START = 1.0
EPSILON_END   = 0.05
NUM_EPISODES  = 120_000
STEPS_PER_EP  = 25     # combat steps per episode

NUM_STATES    = 18     # 3 * 3 * 2
NUM_ACTIONS   = 4

ACTION_NAMES = ["Chase", "Flank", "RetreatVillager", "GroupUp"]

# ── State encode / decode ─────────────────────────────────────────────────────

def encode(player_hp, dist, pressure):
    return player_hp * 6 + dist * 2 + pressure

def decode(state):
    pressure  = state % 2
    rest      = state // 2
    dist      = rest % 3
    player_hp = rest // 3
    return player_hp, dist, pressure

# ── Simulation environment ────────────────────────────────────────────────────
# Each call simulates one "decision tick" in the game and returns (reward, next_state).
# Probabilities are calibrated to match what actually happens in combat:
#   - A healthy player is harder to hit and hits back more
#   - Flanking reduces damage taken at a slight movement cost
#   - Retreating to a villager is always safe but sacrifices hero pressure
#   - Grouping up has small benefit — useful mainly when under heavy fire

def simulate_step(state, action):
    player_hp, dist, pressure = decode(state)
    reward = 0.02   # small survival reward every step

    new_player_hp = player_hp
    new_dist      = dist
    new_pressure  = pressure

    # Hit reward scales dramatically with player weakness — finishing off a
    # dying player is far more valuable than chipping a healthy one.
    HIT_REWARD   = [2.5, 0.9, 0.35][player_hp]   # low/mid/high HP player
    # Damage taken scales with how healthy (and aggressive) the player is.
    DMG_CHANCE   = [0.15, 0.28, 0.45][player_hp]
    DEATH_CHANCE = 0.18 if pressure == 1 else 0.0

    if action == 0:  # ── Chase (direct attack) ─────────────────────────────
        if dist == 0:
            hit_chance = [0.60, 0.40, 0.25][player_hp]
            if random.random() < hit_chance:
                reward += HIT_REWARD
                new_player_hp = max(0, player_hp - 1)
            if random.random() < DMG_CHANCE + pressure * 0.10:
                reward -= 0.5
                new_pressure = min(1, pressure + 1)
            if random.random() < DEATH_CHANCE:
                reward -= 3.0
        elif dist == 1:
            new_dist = 0
        else:
            new_dist = 1

    elif action == 1:  # ── Flank (arc approach — safer, slightly lower payoff) ──
        if dist == 0:
            hit_chance = [0.58, 0.42, 0.30][player_hp]
            if random.random() < hit_chance:
                reward += HIT_REWARD
                new_player_hp = max(0, player_hp - 1)
            # Flanking exposes the zombie less — lower damage received
            if random.random() < DMG_CHANCE * 0.60 + pressure * 0.06:
                reward -= 0.5
                new_pressure = min(1, pressure + 1)
            if random.random() < DEATH_CHANCE * 0.55:
                reward -= 3.0
        elif dist == 1:
            new_dist = 0
        else:
            new_dist = 1

    elif action == 2:  # ── Retreat to Villager ──────────────────────────────
        if pressure == 1:
            reward += 0.70   # genuinely smart when under fire
        else:
            reward -= 0.05   # essentially neutral when not threatened

        # Heavy penalty for retreating when player is already low — cowardly
        if player_hp == 0:
            reward -= 1.20

        new_pressure = max(0, pressure - 1)
        # Zombie hits a villager and circles back — returns to MEDIUM distance,
        # not far. This prevents an infinite "always retreat" cycle.
        new_dist = 1

    elif action == 3:  # ── Group Up ─────────────────────────────────────────
        reward += 0.08 + (0.18 if dist >= 1 else 0.0)   # better value when not already close
        if random.random() < 0.35:
            new_pressure = max(0, pressure - 1)

    next_state = encode(new_player_hp, new_dist, new_pressure)
    return reward, next_state

# ── Q-Learning training loop ──────────────────────────────────────────────────

Q = np.zeros((NUM_STATES, NUM_ACTIONS))

print(f"Training {NUM_EPISODES:,} episodes...")

for episode in range(NUM_EPISODES):
    epsilon = max(EPSILON_END,
                  EPSILON_START - (EPSILON_START - EPSILON_END) * (episode / (NUM_EPISODES * 0.85)))

    # Start from a uniformly random state each episode
    state = random.randint(0, NUM_STATES - 1)

    for _ in range(STEPS_PER_EP):
        # Epsilon-greedy action selection
        if random.random() < epsilon:
            action = random.randint(0, NUM_ACTIONS - 1)
        else:
            action = int(np.argmax(Q[state]))

        reward, next_state = simulate_step(state, action)

        # Bellman update
        Q[state, action] += ALPHA * (
            reward + GAMMA * np.max(Q[next_state]) - Q[state, action]
        )
        state = next_state

    if (episode + 1) % 20_000 == 0:
        print(f"  Episode {episode + 1:,} / {NUM_EPISODES:,}  ε={epsilon:.3f}")

# ── Results ───────────────────────────────────────────────────────────────────

print("\n✓ Training complete.\n")
print("Learned policy (best action per state):")
print(f"{'State':>5}  {'PlayerHP':>8}  {'Distance':>8}  {'Pressure':>8}  {'Best Action':>14}  {'Q-Values'}")
print("-" * 80)

hp_labels   = ["Low", "Mid", "High"]
dist_labels = ["Close", "Medium", "Far"]
pres_labels = ["Low", "High"]

for s in range(NUM_STATES):
    ph, d, p = decode(s)
    best = int(np.argmax(Q[s]))
    qvals = "  ".join(f"{Q[s, a]:+.3f}" for a in range(NUM_ACTIONS))
    print(f"  {s:>3}  {hp_labels[ph]:>8}  {dist_labels[d]:>8}  {pres_labels[p]:>8}  "
          f"{ACTION_NAMES[best]:>14}  {qvals}")

# ── Save to file ──────────────────────────────────────────────────────────────

out_path = os.path.join(os.path.dirname(__file__), "..", "StreamingAssets", "qtable_pretrained.txt")
os.makedirs(os.path.dirname(out_path), exist_ok=True)
np.savetxt(out_path, Q, fmt="%.5f")

print(f"\nSaved: {os.path.abspath(out_path)}")
print("Copy Assets/StreamingAssets/qtable_pretrained.txt into your Unity project")
print("ZombieQLearner.cs will load it automatically at startup.")
