# Huddle app icon

Ring of dots, multi-colour, transparent background. Two variants of the same
mark: seven dots around a neutral centre for 32px and up, five fatter dots for
16-24px where the seven-dot ring turns to mush.

    svg/huddle.svg          vector master, 512 viewBox (>=32px)
    svg/huddle-small.svg    simplified variant (16-24px)
    png/huddle-<n>.png      16 20 24 32 40 48 64 128 192 256 512 1024, transparent
    windows/huddle.ico      9 frames, 16-256, each rendered at its own size
    web/favicon.ico         16 24 32 48
    web/icon-192.png        PWA manifest icon
    web/icon-512.png        PWA manifest icon
    web/huddle.svg          copy of the master, for <link rel="icon">
    web/apple-touch-icon.png 180x180, opaque white ground (iOS blackens alpha)
    web/site.webmanifest    drop-in manifest
    web/head-snippet.html   the <head> tags to paste
    gen.py                  generator - geometry defined once, run to rebuild

## Palette

    #E2593B vermilion   #EE9B33 amber      #57A83E green     #2FA189 teal
    #3C8CCE blue        #7B60D4 violet     #BF57A6 magenta
    #5E6672 slate       (centre dot - mid-tone so it reads on light AND dark)

## Rebuilding

    python3 gen.py

Requires Pillow. Every asset, including both .ico files, is regenerated from
the geometry at the top of gen.py, so changing a radius or a colour there
propagates everywhere.

## Not included

No maskable PWA icon. Android crops maskable icons to a circle and requires an
opaque background filling the frame - incompatible with a transparent
mark-only icon. A tiled variant with the mark inside the 80% safe zone can be
added if you want one.
