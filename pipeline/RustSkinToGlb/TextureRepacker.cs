using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RustSkinToGlb;

// unity spec/gloss -> glTF metal/rough ORM texture (R = occlusion, G = roughness, B = metallic)
// only an approximation, good enough for a viewer
public static class TextureRepacker
{
    public sealed record OrmResult(Image<Rgb24> Orm, Image<Rgba32> BaseColor);

    // how much of the mesh (sampled at triangle centres and edge midpoints) sits on alpha ~0 in the main texture.
    // catches skins that hide parts with alpha 0 while _Cutoff says 1 (the ropes on 3783739172)
    public static double MeshTransparentFraction(string mainTexPath, IReadOnlyList<(float u, float v)> uvs, IReadOnlyList<int> indices, byte alphaBelow = 8)
    {
        using Image<Rgba32> img = Image.Load<Rgba32>(mainTexPath);
        long hit = 0, total = 0;
        bool Sample(float u, float v)
        {
            u -= MathF.Floor(u); v -= MathF.Floor(v);
            int x = Math.Clamp((int)(u * img.Width), 0, img.Width - 1);
            int y = Math.Clamp((int)((1f - v) * img.Height), 0, img.Height - 1);   // glTF/image row 0 = top
            return img[x, y].A < alphaBelow;
        }
        for (int t = 0; t + 2 < indices.Count; t += 3)
        {
            var a = uvs[indices[t]]; var b = uvs[indices[t + 1]]; var c = uvs[indices[t + 2]];
            (float, float)[] pts =
            {
                ((a.u + b.u + c.u) / 3f, (a.v + b.v + c.v) / 3f),
                ((a.u + b.u) / 2f, (a.v + b.v) / 2f), ((b.u + c.u) / 2f, (b.v + c.v) / 2f), ((a.u + c.u) / 2f, (a.v + c.v) / 2f),
            };
            foreach (var (pu, pv) in pts) { total++; if (Sample(pu, pv)) hit++; }
        }
        return total == 0 ? 0 : (double)hit / total;
    }

    // metals have a near black diffuse here, their colour is in the spec map.
    // metallicLower/Upper: spec brightness goes from 0% metal at Lower to 100% at Upper (the maps are dark,
    // a gold skin averages 0.39-0.71). Base colour is the diffuse blended toward the spec colour by that amount.
    // metallicGlossPath is the other workflow (R = metallic, A = smoothness), maps straight over.
    // dielectric forces non-metal
    public static OrmResult BuildOrmAndBaseColor(string mainTexPath, string? specGlossPath, string? occlusionPath, float glossinessScale = 1f, float occlusionStrength = 1f, float metallicLower = 0.12f, float metallicUpper = 0.6f, string? metallicGlossPath = null, bool dielectric = false)
    {
        using Image<Rgba32> mainTex = Image.Load<Rgba32>(mainTexPath);
        using Image<Rgba32>? specGloss = specGlossPath is not null ? Image.Load<Rgba32>(specGlossPath) : null;
        // only used when there's no spec map (a skin has one or the other)
        using Image<Rgba32>? metalGloss = (specGlossPath is null && metallicGlossPath is not null)
            ? Image.Load<Rgba32>(metallicGlossPath) : null;
        using Image<L8>? occlusion = occlusionPath is not null ? Image.Load<L8>(occlusionPath) : null;

        int width = specGloss?.Width ?? metalGloss?.Width ?? mainTex.Width;
        int height = specGloss?.Height ?? metalGloss?.Height ?? mainTex.Height;

        var orm = new Image<Rgb24>(width, height);
        var baseColor = new Image<Rgba32>(width, height);

        // scale the inputs to the same size if they differ (parts can have different resolutions)
        Image<Rgba32> mt = mainTex;
        bool ownsMt = false;
        if (mt.Width != width || mt.Height != height)
        {
            mt = mt.Clone(ctx => ctx.Resize(width, height));
            ownsMt = true;
        }

        Image<Rgba32>? sg = specGloss;
        bool ownsSg = false;
        if (sg is not null && (sg.Width != width || sg.Height != height))
        {
            sg = sg.Clone(ctx => ctx.Resize(width, height));
            ownsSg = true;
        }

        Image<Rgba32>? mg = metalGloss;
        bool ownsMg = false;
        if (mg is not null && (mg.Width != width || mg.Height != height))
        {
            mg = mg.Clone(ctx => ctx.Resize(width, height));
            ownsMg = true;
        }

        Image<L8>? ao = occlusion;
        bool ownsAo = false;
        if (ao is not null && (ao.Width != width || ao.Height != height))
        {
            ao = ao.Clone(ctx => ctx.Resize(width, height));
            ownsAo = true;
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte aoTexVal = ao is not null ? ao[x, y].PackedValue : (byte)255;
                // occlusion strength mixes between "no occlusion" (255) and the texture
                float aoF = 255f + (aoTexVal - 255f) * occlusionStrength;
                byte aoVal = (byte)Math.Clamp(aoF, 0, 255);

                var diffuse = mt[x, y];

                byte roughness = 255; // fully rough when there's no spec map
                byte metallic = 0;
                var bc = new Rgba32(diffuse.R, diffuse.G, diffuse.B, diffuse.A);

                if (sg is not null)
                {
                    var px = sg[x, y];
                    float glossiness = Math.Clamp(px.A / 255f * glossinessScale, 0f, 1f);
                    // some skins have a normal map sitting in the spec slot (the guitar skin's whole spec map was one).
                    // A flat normal is about R 128, G 128, B 255. Read as a spec map that gives a perfect mirror
                    // instead of a dark guitar, so skip those pixels.
                    bool isNormalMapPixel = Math.Abs(px.R - 128) < 40 && Math.Abs(px.G - 128) < 40 && px.B > 200;
                    if (!isNormalMapPixel)
                    {

                    roughness = (byte)Math.Clamp((1f - glossiness) * 255f, 0, 255);

                    float specIntensity = (px.R + px.G + px.B) / (3f * 255f);
                    float metalF = Math.Clamp(
                        (specIntensity - metallicLower) / (metallicUpper - metallicLower), 0f, 1f);
                    metallic = (byte)Math.Clamp(metalF * 255f, 0, 255);

                    // no spec -> base colour blend when the spec is very blue (B/G over 1.5), that's a gloss tint
                    // and not a metal colour. guitar B/G ~2.9 no blend, python ~1.0 half, gold AK47 ~0.3 full
                    float blueRatio = px.B / (float)Math.Max((int)px.G, 1);
                    float blendScale = Math.Clamp(1.5f - blueRatio, 0f, 1f);
                    float blendF = metalF * blendScale;

                    byte r = (byte)Math.Clamp(diffuse.R * (1f - blendF) + px.R * blendF, 0, 255);
                    byte g = (byte)Math.Clamp(diffuse.G * (1f - blendF) + px.G * blendF, 0, 255);
                    byte b = (byte)Math.Clamp(diffuse.B * (1f - blendF) + px.B * blendF, 0, 255);
                    bc = new Rgba32(r, g, b, diffuse.A);

                    // cloth (backpacks) is never metal. Their spec maps have painted detail (seams, prints) at mid
                    // grey levels, and the ramp above reads that as partly metal, so black fabric turned into silver foil
                    if (dielectric)
                    {
                        metallic = 0;
                        bc = new Rgba32(diffuse.R, diffuse.G, diffuse.B, diffuse.A);
                    }

                    } // end !isNormalMapPixel
                }
                else if (mg is not null)
                {
                    // unity metallic setup (R = metallic, A = smoothness), maps straight over,
                    // the base colour stays the plain albedo
                    var px = mg[x, y];
                    float glossiness = Math.Clamp(px.A / 255f * glossinessScale, 0f, 1f);
                    roughness = (byte)Math.Clamp((1f - glossiness) * 255f, 0, 255);
                    metallic = px.R;
                }

                orm[x, y] = new Rgb24(aoVal, roughness, metallic);
                baseColor[x, y] = bc;
            }
        }

        if (ownsMt) mt.Dispose();
        if (ownsSg) sg!.Dispose();
        if (ownsMg) mg!.Dispose();
        if (ownsAo) ao!.Dispose();

        return new OrmResult(orm, baseColor);
    }

    // some groups have a completely empty MainTex (RGBA 0,0,0,0 everywhere), the skin probably doesn't
    // retexture that part so the caller skips it. Only alpha 0 everywhere counts, comparing max alpha to
    // cutoff * 255 blanked hundreds of normal skins (alpha is just leftover data when _Cutoff is 0 or 1).
    // The DBS stock group on 916790605 / 1229950256 has white RGB under the alpha 0
    public static bool IsBlankMainTexture(string path, float cutoff = 0f)
    {
        using Image<Rgba32> img = Image.Load<Rgba32>(path);
        bool allAlphaZero = true;
        for (int y = 0; y < img.Height; y++)
        {
            for (int x = 0; x < img.Width; x++)
            {
                var px = img[x, y];
                if (px.A != 0) allAlphaZero = false;
                if (cutoff <= 0f && (px.A != 0 || px.R != 0 || px.G != 0 || px.B != 0)) return false;
            }
        }
        if (cutoff <= 0f) return true;
        return allAlphaZero;
    }

    // some skins have a plain RGB normal map and not unity's DXT5nm layout. Decoding those as DXT5nm gives every
    // pixel the same X and the shading is garbage (the crumpled Oil King backpack).
    // checked on a sample: alpha nearly constant and red varying = plain
    public static bool IsPlainRgbNormalMap(Image<Rgba32> src)
    {
        int aMin = 255, aMax = 0, rMin = 255, rMax = 0;
        int sx = Math.Max(1, src.Width / 64), sy = Math.Max(1, src.Height / 64);
        for (int y = 0; y < src.Height; y += sy)
            for (int x = 0; x < src.Width; x += sx)
            {
                var p = src[x, y];
                aMin = Math.Min(aMin, p.A); aMax = Math.Max(aMax, p.A);
                rMin = Math.Min(rMin, p.R); rMax = Math.Max(rMax, p.R);
            }
        return (aMax - aMin) < 8 && (rMax - rMin) > 40;
    }

    public static bool IsPlainRgbNormalMap(string path)
    {
        using var img = Image.Load<Rgba32>(path);
        return IsPlainRgbNormalMap(img);
    }

    // unity's BumpMap is DXT5nm style: R constant 255, X in alpha, Y in green, no Z. glTF wants plain RGB = XYZ so this
    // rebuilds it (X = A, Y = G, Z = sqrt(1 - X^2 - Y^2)), plain RGB maps are read directly.
    // bumpScale is _BumpScale, it scales X/Y before Z is worked out
    public static Image<Rgb24> UnpackUnityNormalMap(string bumpMapPath, float bumpScale = 1f)
    {
        using Image<Rgba32> src = Image.Load<Rgba32>(bumpMapPath);
        var result = new Image<Rgb24>(src.Width, src.Height);
        bool plain = IsPlainRgbNormalMap(src);

        for (int y = 0; y < src.Height; y++)
        {
            for (int x = 0; x < src.Width; x++)
            {
                var px = src[x, y];

                float nx = Math.Clamp(((plain ? px.R : px.A) / 255f * 2f - 1f) * bumpScale, -1f, 1f);
                float ny = Math.Clamp((px.G / 255f * 2f - 1f) * bumpScale, -1f, 1f);
                float nz = MathF.Sqrt(Math.Clamp(1f - nx * nx - ny * ny, 0f, 1f));

                byte r = (byte)Math.Clamp((nx * 0.5f + 0.5f) * 255f, 0, 255);
                byte g = (byte)Math.Clamp((ny * 0.5f + 0.5f) * 255f, 0, 255);
                byte b = (byte)Math.Clamp((nz * 0.5f + 0.5f) * 255f, 0, 255);

                result[x, y] = new Rgb24(r, g, b);
            }
        }

        return result;
    }
}