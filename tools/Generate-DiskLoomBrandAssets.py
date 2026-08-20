from __future__ import annotations

import argparse
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "DiskLoom" / "Assets"
MASTER = ASSETS / "DiskLoom-Mark-Master.png"


def alpha_fitted_square(source: Image.Image, size: int = 1024, padding: int = 84) -> Image.Image:
    rgba = source.convert("RGBA")
    alpha_box = rgba.getchannel("A").getbbox()
    if alpha_box is None:
        raise ValueError("The master logo has no visible alpha content.")
    cropped = rgba.crop(alpha_box)
    available = size - padding * 2
    cropped.thumbnail((available, available), Image.Resampling.LANCZOS)
    result = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    x = (size - cropped.width) // 2
    y = (size - cropped.height) // 2
    result.alpha_composite(cropped, (x, y))
    return result


def font(size: int) -> ImageFont.FreeTypeFont:
    candidates = [
        Path(r"C:\Windows\Fonts\seguisb.ttf"),
        Path(r"C:\Windows\Fonts\segoeuib.ttf"),
        Path(r"C:\Windows\Fonts\arialbd.ttf"),
    ]
    for candidate in candidates:
        if candidate.exists():
            return ImageFont.truetype(str(candidate), size=size)
    return ImageFont.load_default(size=size)


def make_lockup(mark: Image.Image, foreground: tuple[int, int, int, int], destination: Path) -> None:
    canvas = Image.new("RGBA", (1800, 560), (0, 0, 0, 0))
    icon = mark.resize((480, 480), Image.Resampling.LANCZOS)
    canvas.alpha_composite(icon, (18, 40))
    draw = ImageDraw.Draw(canvas)
    label_font = font(248)
    draw.text((510, 128), "DiskLoom", font=label_font, fill=foreground, stroke_width=0)
    canvas.save(destination, optimize=True)


def make_installer_art(mark: Image.Image) -> None:
    banner = Image.new("RGB", (493, 58), (244, 249, 255))
    banner_icon = mark.resize((52, 52), Image.Resampling.LANCZOS)
    # WixUI draws the page title and subtitle over the left side of this image.
    # Keep that text-safe area empty and place branding at the far right.
    banner.paste(banner_icon, (435, 3), banner_icon)
    banner.save(ASSETS / "InstallerBanner.png", optimize=True)

    dialog = Image.new("RGB", (493, 312), (246, 250, 255))
    dialog_draw = ImageDraw.Draw(dialog)
    for y in range(dialog.height):
        amount = y / max(1, dialog.height - 1)
        color = (
            int(246 - 12 * amount),
            int(250 - 6 * amount),
            255,
        )
        dialog_draw.line((0, y, dialog.width, y), fill=color)
    # Standard WixUI dialogs reserve the left strip for artwork and put their
    # controls to its right. Keep the mark wholly inside that strip.
    dialog_icon = mark.resize((112, 112), Image.Resampling.LANCZOS)
    dialog.paste(dialog_icon, (14, 100), dialog_icon)
    dialog.save(ASSETS / "InstallerDialog.png", optimize=True)


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate DiskLoom brand assets.")
    parser.add_argument(
        "--installer-only",
        action="store_true",
        help="Regenerate only the WixUI banner and dialog artwork.",
    )
    args = parser.parse_args()

    ASSETS.mkdir(parents=True, exist_ok=True)
    mark = alpha_fitted_square(Image.open(MASTER))
    if args.installer_only:
        make_installer_art(mark)
        return

    mark.save(ASSETS / "DiskLoom-Mark.png", optimize=True)

    for size in (16, 24, 32, 48, 64, 128, 256, 512):
        mark.resize((size, size), Image.Resampling.LANCZOS).save(
            ASSETS / f"AppIcon-{size}.png", optimize=True
        )

    mark.save(
        ASSETS / "AppIcon.ico",
        format="ICO",
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
    )
    make_lockup(mark, (24, 31, 42, 255), ASSETS / "DiskLoom-Logo-Dark.png")
    make_lockup(mark, (248, 250, 252, 255), ASSETS / "DiskLoom-Logo-Light.png")
    make_installer_art(mark)


if __name__ == "__main__":
    main()
