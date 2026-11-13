using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RustSkinToGlb;

// Textures for the viewer's in-world scene (docs/world): the game's building atlas (atlas_<name>_bc / nrm / mg / ao, exported with
// AssetStudio) turned into what three.js wants: a colour map, an unpacked normal map and an ORM map (R occlusion, G roughness,
// B metalness), at <size> pixels and as JPEG so the site stays light.
//   RustSkinToGlb --build-world-textures <atlas dir with the 4 PNGs> <atlas name> <out dir> <size>
public static partial class Program
{
    // RustSkinToGlb --world-texture <in.png> <out.jpg|out.png> <size> [normal]: one scenery texture, resized to <size> pixels on the
    // long side. "normal" unpacks Unity's DXT5nm normal map into a plain RGB one. PNG keeps the alpha (leaves), JPEG is smaller
    private static int WorldTextureMain(string[] a)
    {
        string src = a[0], dst = a[1];
        int size = int.Parse(a[2]);
        bool normal = a.Length > 3 && a[3] == "normal";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dst))!);
        Image img = normal ? TextureRepacker.UnpackUnityNormalMap(src) : Image.Load<Rgba32>(src);
        using (img)
        {
            int max = Math.Max(img.Width, img.Height);
            if (max > size) img.Mutate(c => c.Resize(img.Width * size / max, img.Height * size / max));
            if (dst.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) img.SaveAsPng(dst);
            else img.SaveAsJpeg(dst, new JpegEncoder { Quality = 88 });
        }
        Console.WriteLine($"wrote {dst}");
        return 0;
    }

    private static int BuildWorldTexturesMain(string[] a)
    {
        string dir = a[0], name = a[1], outDir = a[2];
        int size = a.Length > 3 ? int.Parse(a[3]) : 2048;
        Directory.CreateDirectory(outDir);
        string Src(string kind) => Directory.GetFiles(dir, $"{name}_{kind}.png", SearchOption.AllDirectories).First();

        var result = TextureRepacker.BuildOrmAndBaseColor(Src("bc"), null, Src("ao"), metallicGlossPath: Src("mg"));
        void Save(Image img, string file)
        {
            img.Mutate(c => c.Resize(size, size));
            img.SaveAsJpeg(Path.Combine(outDir, file), new JpegEncoder { Quality = 88 });
            Console.WriteLine($"wrote {file}");
        }
        using (result.BaseColor) Save(result.BaseColor, $"{name}_color.jpg");
        using (result.Orm) Save(result.Orm, $"{name}_orm.jpg");
        using var normal = TextureRepacker.UnpackUnityNormalMap(Src("nrm"));
        Save(normal, $"{name}_normal.jpg");
        return 0;
    }
}
