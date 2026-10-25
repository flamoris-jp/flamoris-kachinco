# Per-shaped-character wave, with a small harmonic sum.
def animate(t, duration, i, n, seed):
    wave = 0
    for k in range(1, 4):
        wave += sin(t * 4 * k - i * 0.4) * (8 / k)
    fade = 1 - smoothstep(max(0, duration - 0.3), duration, t)
    return {'y': wave, 'opacity': fade}
