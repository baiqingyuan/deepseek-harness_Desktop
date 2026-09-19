# -*- coding: utf-8 -*-
"""
从设计稿截图生成多尺寸 Windows 图标（icons/DeepSeekHarness.ico）。

背景：设计稿是一张在深色底上截的 PNG（圆角外是深色背景、且顶部被裁掉约 3px），
直接把截图塞进 .ico 会在浅色背景上露出深色直角块，而且小尺寸靠系统缩放会很糊。

本脚本做的事：
  1. 以“背景色距离”为参考，还原圆角形状（解析式圆角矩形 + 16x 超采样）；
  2. 对边缘像素做 un-premultiply（obs = a*C + (1-a)*bg 反解 C），去掉边缘的深色描边；
  3. 顶部缺失的约 3px 用首行像素补齐；
  4. 逐尺寸用 LANCZOS 直接缩放（每个尺寸从 187px 母版缩，而不是从 256 逐级缩），
     256 尺寸为放大，做一次轻度 unsharp 补偿；
  5. 输出多尺寸 .ico（16/20/24/32/40/48/64/96/128/256）。

用法：python icons/build-icon.py
"""
import os
import numpy as np
from PIL import Image, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "source", "icon-ref.png")
OUT_ICO = os.path.join(HERE, "DeepSeekHarness.ico")
OUT_MASTER = os.path.join(HERE, "source", "icon-master.png")
OUT_PREVIEW = os.path.join(HERE, "source", "icon-preview.png")

# --- 从设计稿量出来的几何量（单位：截图像素） -------------------------------
BODY_X = 2.4          # 图标主体左边缘
BODY_Y = -2.5         # 图标主体上边缘（截图顶部裁掉了约 2.5px）
BODY_W = 187          # 主体宽（= 高，正方形）
RADIUS = 20.0         # 圆角半径
BG = np.array([20.0, 35.0, 64.0])   # 截图里的深色底
SS = 16               # 圆角遮罩超采样倍数


def rounded_alpha(w, h, radius):
    """返回 w*h 的浮点 alpha 遮罩（解析式圆角矩形，抗锯齿）。"""
    ys, xs = np.mgrid[0:h * SS, 0:w * SS]
    # 超采样坐标系下：像素中心 = (i + 0.5) / SS
    px = (xs + 0.5) / SS
    py = (ys + 0.5) / SS
    r = radius
    x0, y0 = 0.0 + 0.5, 0.0 + 0.5
    x1, y1 = w - 0.5, h - 0.5
    # 到圆角矩形内部的距离场：用 4 个角的圆心做 clamp
    cx = np.clip(px, x0 + r, x1 - r)
    cy = np.clip(py, y0 + r, y1 - r)
    inside = (px >= x0) & (px <= x1) & (py >= y0) & (py <= y1)
    dist = np.sqrt((px - cx) ** 2 + (py - cy) ** 2)
    cov = ((dist <= r) & inside) | ((dist == 0) & inside)
    cov = cov.reshape(h, SS, w, SS).mean(axis=(1, 3))
    return cov


def main():
    src = Image.open(SRC).convert("RGB")
    a = np.asarray(src).astype(float)
    sw, sh = src.size
    print("source:", src.size)

    # 主体在截图里的采样窗口（顶部越界用首行补齐）
    out = np.zeros((BODY_W, BODY_W, 3), dtype=float)
    for y in range(BODY_W):
        sy = int(round(y + BODY_Y))
        sy = max(0, min(sh - 1, sy))
        for x in range(BODY_W):
            sx = int(round(x + BODY_X))
            sx = max(0, min(sw - 1, sx))
            out[y, x] = a[sy, sx]

    alpha = rounded_alpha(BODY_W, BODY_W, RADIUS)

    # 边缘 un-premultiply：obs = al*C + (1-al)*bg  =>  C = (obs-(1-al)*bg)/al
    al = alpha[:, :, None]
    safe = np.clip(al, 1e-3, 1.0)
    un = (out - (1 - al) * BG) / safe
    un = np.clip(un, 0, 255)
    rgb = np.where(al > 0.35, un, out)

    master = np.concatenate([rgb, (alpha * 255)[:, :, None]], axis=2).astype(np.uint8)
    # 轻微羽化，消掉超采样遮罩在极小半径上的硬边
    master_img = Image.fromarray(master, "RGBA")
    master_img.putalpha(master_img.getchannel("A").filter(ImageFilter.GaussianBlur(0.35)))
    os.makedirs(os.path.dirname(OUT_MASTER), exist_ok=True)
    master_img.save(OUT_MASTER)
    print("master:", master_img.size, "->", OUT_MASTER)

    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    frames = []
    for s in sizes:
        img = master_img.resize((s, s), Image.LANCZOS)
        if s > BODY_W:  # 放大：补一点锐度
            img = img.filter(ImageFilter.UnsharpMask(radius=1.0, percent=60, threshold=0))
        frames.append(img)
        print("  size", s, "ok")

    # ICO 需要从大到小传入，PIL 会按 sizes 参数落盘。
    # bitmap_format 必须是 bmp：Pillow 新版本默认把帧压成 PNG，而 System.Drawing.Icon
    # （.NET Framework 的 Icon.ToBitmap / new Icon(path, size)）解不了 PNG 帧，
    # 会画出全黑或者直接抛异常 —— 桌面壳、托盘、标题栏全都靠它。
    try:
        frames[-1].save(OUT_ICO, format="ICO",
                        sizes=[(f.width, f.height) for f in frames],
                        bitmap_format="bmp")
    except TypeError:
        frames[-1].save(OUT_ICO, format="ICO", sizes=[(f.width, f.height) for f in frames])
    print("ico ->", OUT_ICO, os.path.getsize(OUT_ICO), "bytes")

    # 自检：所有帧都必须是未压缩 BMP（魔数 0x28 = BITMAPINFOHEADER 长度 40）
    import struct
    raw = open(OUT_ICO, "rb").read()
    n = struct.unpack_from("<H", raw, 4)[0]
    off = 6
    for i in range(n):
        w, h, cc, rsv, planes, bpp, size, offset = struct.unpack_from("<BBBBHHII", raw, off)
        fmt = "PNG" if raw[offset:offset + 4] == b"\x89PNG" else "BMP"
        print("  frame %sx%s %s" % (w or 256, h or 256, fmt))
        if fmt != "BMP":
            raise SystemExit("ICO 帧被压成 PNG 了，System.Drawing 解不了（需要 bitmap_format=bmp）")
        off += 16

    # 预览图：浅底 / 深底各一行，按原始尺寸并排（96/128/256 单独缩小展示）
    pad = 10
    cells = []
    for f in frames:
        w = min(f.width, 96)
        img = f if f.width <= 96 else f.resize((96, 96), Image.LANCZOS)
        cells.append((max(w, 16), img))
    sheet_w = sum(w for w, _ in cells) + pad * (len(cells) + 1)
    row_h = max(h for _, im in cells for h in [im.height]) + pad
    sheet = Image.new("RGB", (sheet_w, 2 * (row_h + pad) + pad), (245, 246, 248))
    for row, bg in enumerate([(245, 246, 248), (26, 27, 31)]):
        y = pad + row * (row_h + pad)
        x = pad
        for w, img in cells:
            tile = Image.new("RGBA", (w, img.height), bg + (255,))
            tile.alpha_composite(img, ((w - img.width) // 2, 0))
            sheet.paste(tile.convert("RGB"), (x, y))
            x += w + pad
    sheet.save(OUT_PREVIEW)
    print("preview ->", OUT_PREVIEW)


if __name__ == "__main__":
    main()
