namespace RustSkinToGlb;

// Paths relative to the repo, so it works wherever it's checked out.
// These are properties and not static fields on purpose, ItemConfigs is a static initializer in another
// part of this class and would depend on field init order across the files.
public static partial class Program
{
    // the pipeline folder (has data/ and RustSkinToGlb/ in it)
    internal static string PipelineRoot => FindRepoRoot();

    // game meshes pulled out of content.bundle, checked in with the code
    internal static string GameMeshRefs => Path.Combine(PipelineRoot, "RustSkinToGlb", "GameMeshRefs");

    // extracted skin textures + manifests: data/all-skins-textures/assets/skins/<id>/...
    internal static string DefaultSkinsRoot => Path.Combine(PipelineRoot, "data", "all-skins-textures", "assets", "skins");

    // where the site looks for models, the weekly job builds straight into it
    internal static string DefaultGlbDir => Path.GetFullPath(Path.Combine(PipelineRoot, "..", "docs", "models"));
}
