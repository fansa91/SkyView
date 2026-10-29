"""生成 SkyView 应用图标（Fluent / WinUI 3 风格的圆角渐变磁贴 + 太阳/云字形）。

设计约束（都是为了让 16px 下还能认出来）：
- **圆角磁贴**：半径取边长的 22%，这是 Fluent 图标的通行比例；
  太方像 UWP 时代的老图标，太圆会被看成圆形头像。
- **渐变从左上到右下**：比纯色更有体积感，且和 WinUI 的 Acrylic 高光方向一致。
- **字形只用两个形状**：太阳（暖黄）+ 云（纯白）。三色以上在 16px 下会糊成一团，
  所以不画雨滴、不画阴影层数、不描边。
- **云压住太阳右下**：太阳露出一角，形成"晴间多云"的语义，
  并且在极小尺寸下靠"上方一点黄 + 下方一块白"就能区分晴天/阴天。

输出：
  src/SkyView/Assets/SkyView.ico   （16/20/24/32/40/48/64/128/256 多尺寸）
  src/SkyView/Assets/SkyView.png   （1024×1024，README 与商店素材用）
  docs/logo-banner.png             （README 顶部的宽幅横幅）

用法：python tools/make_icon.py
"""

from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

REPO_ROOT = Path(__file__).resolve().parent.parent
ASSETS = REPO_ROOT / "src" / "SkyView" / "Assets"
DOCS = REPO_ROOT / "docs"

# 所有几何都按 1024 设计稿写死，再按 SS 倍超采样绘制后缩回去 —— 直接在小尺寸上
# 画圆角/斜线会有锯齿，超采样是最省事的抗锯齿手段。
SS = 2
SIZE = 1024
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

# 磁贴渐变（Fluent 天蓝）。上端偏亮、下端偏深，模拟顶光。
TOP_COLOR = (76, 194, 255)
BOTTOM_COLOR = (0, 120, 212)

SUN_COLOR = (255, 209, 102)
CLOUD_COLOR = (255, 255, 255)
SHADOW_COLOR = (0, 65, 120, 44)


def _vertical_gradient(width: int, height: int) -> Image.Image:
    """逐行填充做的线性渐变。PIL 没有内建 gradient，逐行画是最直接的写法。"""
    image = Image.new("RGB", (width, height))
    draw = ImageDraw.Draw(image)
    for y in range(height):
        t = y / max(1, height - 1)
        color = tuple(
            round(TOP_COLOR[i] + (BOTTOM_COLOR[i] - TOP_COLOR[i]) * t) for i in range(3)
        )
        draw.line([(0, y), (width, y)], fill=color)
    return image


def _rounded_mask(width: int, height: int, radius: int) -> Image.Image:
    mask = Image.new("L", (width, height), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, width - 1, height - 1], radius=radius, fill=255)
    return mask


def _draw_sun(
    draw: ImageDraw.ImageDraw,
    cx: float,
    cy: float,
    r: float,
    rays: int = 8,
    skip_angles: tuple[float, ...] = (90.0,),
) -> None:
    """太阳 = 实心圆 + 圆头光芒。

    光芒用 `line(width=...)` 再在末端补一个圆来做圆头，
    因为 PIL 的 line 没有 cap 参数。

    `skip_angles` 默认跳过正下方那一道：那个位置会被云整个盖住，
    只会在云的轮廓上方露出一小块黄色，看起来像粘在云上的碎片。
    """
    ray_inner = r * 1.42
    ray_outer = r * 1.95
    ray_width = max(1, int(r * 0.30))

    for i in range(rays):
        angle = i * (360 / rays)
        if angle in skip_angles:
            continue

        rad = angle * math.pi / 180
        x1 = cx + ray_inner * math.cos(rad)
        y1 = cy + ray_inner * math.sin(rad)
        x2 = cx + ray_outer * math.cos(rad)
        y2 = cy + ray_outer * math.sin(rad)
        draw.line([(x1, y1), (x2, y2)], fill=SUN_COLOR, width=ray_width)
        cap = ray_width / 2
        draw.ellipse([x2 - cap, y2 - cap, x2 + cap, y2 + cap], fill=SUN_COLOR)

    draw.ellipse([cx - r, cy - r, cx + r, cy + r], fill=SUN_COLOR)


def _draw_cloud(draw: ImageDraw.ImageDraw, cx: float, cy: float, w: float) -> None:
    """云 = 圆角长条 + 三个鼓包（小 / 大 / 中）。

    两个关键几何约束：

    1. 鼓包的**圆心压在长条顶边上**，让每个圆的下半部分藏进长条里，
       三个图形并起来才是平滑的云轮廓。
    2. 相邻鼓包的**圆心距要小于半径和**（即必须相交），否则中间会留下一道
       又深又尖的 V 形夹缝，看着像缝合线而不是云。
    """
    h = w * 0.62
    bottom = cy + h / 2
    bar_h = h * 0.40
    bar_top = bottom - bar_h

    # 底部圆角长条
    draw.rounded_rectangle(
        [cx - w / 2, bar_top, cx + w / 2, bottom],
        radius=bar_h / 2,
        fill=CLOUD_COLOR,
    )

    # 三个鼓包：左小、中最大、右中等。中间的负责填掉两侧的夹缝。
    for offset_x, radius in (
        (-w * 0.26, h * 0.26),
        (-w * 0.02, h * 0.36),
        (w * 0.23, h * 0.30),
    ):
        x = cx + offset_x
        draw.ellipse(
            [x - radius, bar_top - radius, x + radius, bar_top + radius],
            fill=CLOUD_COLOR,
        )


def render(size: int = SIZE) -> Image.Image:
    """画一张 size×size 的 RGBA 图标。"""
    s = size * SS
    canvas = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # 磁贴留一点外边距，任务栏/资源管理器里才不会顶满格子
    inset = int(s * 0.055)
    tile_size = s - inset * 2
    radius = int(tile_size * 0.22)

    gradient = _vertical_gradient(tile_size, tile_size).convert("RGBA")
    gradient.putalpha(_rounded_mask(tile_size, tile_size, radius))
    canvas.alpha_composite(gradient, (inset, inset))

    # 字形单独一层，方便给云加投影
    glyph = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glyph)

    _draw_sun(gd, cx=s * 0.615, cy=s * 0.335, r=s * 0.145)

    cloud_layer = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    _draw_cloud(ImageDraw.Draw(cloud_layer), cx=s * 0.465, cy=s * 0.630, w=s * 0.500)

    # 云投影：把云的 alpha 平移几像素再模糊，做出轻微离层感。
    # 偏移和模糊都要很克制，重一点就会在云下面糊出一圈脏边。
    shadow = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    shadow.paste(SHADOW_COLOR, (0, 0), cloud_layer.split()[3])
    shadow = shadow.filter(ImageFilter.GaussianBlur(s * 0.007))
    glyph.alpha_composite(shadow, (0, int(s * 0.006)))

    glyph.alpha_composite(cloud_layer)
    canvas.alpha_composite(glyph)

    return canvas.resize((size, size), Image.LANCZOS)


def render_banner(width: int = 1280, height: int = 400) -> Image.Image:
    """README 顶部横幅：浅色底 + 图标 + 应用名。

    不用深色底 —— README 在 GitHub 上大多以浅色主题阅读。
    """
    banner = Image.new("RGB", (width, height), (255, 255, 255))
    draw = ImageDraw.Draw(banner)

    # 极浅的蓝色斜向渐晕，避免大片纯白显得空
    for y in range(height):
        t = y / max(1, height - 1)
        color = (
            round(255 - 12 * t),
            round(255 - 6 * t),
            255,
        )
        draw.line([(0, y), (width, y)], fill=color)

    icon_size = 220
    icon = render(icon_size)
    banner.paste(icon, (72, (height - icon_size) // 2), icon)

    try:
        from PIL import ImageFont

        font_big = ImageFont.truetype("segoeuib.ttf", 84)
        font_small = ImageFont.truetype("segoeui.ttf", 34)
    except Exception:
        font_big = font_small = None

    text_x = 72 + icon_size + 52
    draw.text((text_x, height // 2 - 82), "SkyView", fill=(20, 32, 44), font=font_big)
    draw.text(
        (text_x, height // 2 + 22),
        "Windows 11 原生的多数据源天气应用",
        fill=(90, 104, 118),
        font=font_small,
    )
    return banner


def main() -> int:
    ASSETS.mkdir(parents=True, exist_ok=True)
    DOCS.mkdir(parents=True, exist_ok=True)

    big = render(SIZE)
    png_path = ASSETS / "SkyView.png"
    big.save(png_path)
    print(f"写出 {png_path.relative_to(REPO_ROOT)}  {big.width}x{big.height}")

    ico_path = ASSETS / "SkyView.ico"
    big.save(ico_path, format="ICO", sizes=[(n, n) for n in ICO_SIZES])
    print(f"写出 {ico_path.relative_to(REPO_ROOT)}  尺寸={ICO_SIZES}")

    banner = render_banner()
    banner_path = DOCS / "logo-banner.png"
    banner.save(banner_path)
    print(f"写出 {banner_path.relative_to(REPO_ROOT)}  {banner.width}x{banner.height}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
