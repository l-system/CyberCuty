# CyberCity

A GPU raymarcher that scans a directory tree and renders it as a city — each
top-level directory becomes a building, with its file listing rendered on the
walls.

## Running it

There's no GUI for picking a directory yet, so after giving it executable status (chmod +x) 
you run this from a terminal / shell , passing the directory you want to visualize as an argument:

```
CyberCity /path/to/some/directory
```

If you don't pass a directory, it'll prompt you for one interactively:

```
CyberCity
=== Cyber City Directory Scanner ===
Enter directory to scan (press Enter for current directory '/home/you'):
```

**Important — expect a delay before anything appears.** The program scans the
entire directory tree (up to the limits below) and finishes building all of
the wall textures *before* the window opens. For a large directory, this can
take a noticeable amount of time with no visual feedback at all — a blank
terminal, no window yet. This is expected; it isn't frozen. Larger/deeper
directories and lower `--max-files`/`--max-depth` values change how long this
takes.

## Command-line options

| Option | Default | Description |
|---|---|---|
| `directory` (positional) | — | Directory to scan. If omitted, you'll be prompted for one. |
| `--width N` | `2880` | Render width in pixels. |
| `--height N` | `1620` | Render height in pixels. |
| `--ior N` | `1.31` (Ice) | Index of refraction for the transparent/glass building material — see table below for reference values. |
| `--max-depth N` | `8` | How many directory levels deep to recurse. |
| `--max-files N` | `10000` | Maximum number of files/directories to scan in total. Lower this for a faster (but less complete) scan on very large directory trees. |
| `--max-directories N` | `1000` | Maximum number of top-level directories rendered as city blocks/buildings. |
| `-h`, `--help` | — | Print usage and exit. |

Example:

```
CyberCity ~/projects --max-files 2000 --max-depth 4 --ior 2.42
```

### Index of refraction reference values

`--ior` accepts any number, but these are real-world reference points if you
want a specific material's look:

| Material | IOR |
|---|---|
| Air | 1.0 |
| Ice | 1.31 (default) |
| Water | 1.333 |
| Ethanol | 1.36 |
| Acrylic | 1.49 |
| Crown Glass | 1.52 |
| Quartz | 1.54 |
| Flint Glass | 1.66 |
| Sapphire | 1.77 |
| Zircon | 1.92 |
| Diamond | 2.42 |
| Silicon | 3.88 |

## Controls

| Input | Action |
|---|---|
| `W` / `S` | Move forward / backward |
| `A` / `D` | Strafe left / right |
| `E` / `Q` | Move up / down |
| Mouse | Look around (captured by default — see below) |
| Shift (either side) | Hold to run |
| `Esc` | Toggle mouse capture on/off |

The mouse is captured (hidden, locked to the window) as soon as the window
opens, so you can look around immediately. Press `Esc` to release it if you
need to reach another window, and press it again to re-capture and resume
looking around.
