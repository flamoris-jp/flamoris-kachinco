# Instant appearance; scatter and fade only during the last 0.35 seconds.
def ease_out(v):
    return 1 - (1 - v) ** 3

def animate(t, duration, i, n, seed):
    span = min(0.35, duration)
    p = clamp((t - duration + span) / span, 0, 1)
    distance = ease_out(p)
    return {
        'x': noise(seed, i, 0) * 100 * distance,
        'y': (30 + noise(seed, i, 1) * 50) * distance,
        'rotation': noise(seed, i, 2) * 35 * distance,
        'opacity': 1 - p,
    }
