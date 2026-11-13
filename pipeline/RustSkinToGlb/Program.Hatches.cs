namespace RustSkinToGlb;

// the part lists of the four ladder hatch item types, as animated nodes (see HatchRig.cs for how the pieces move)
public static partial class Program
{
    // pivots and angles below are in the OBJ's own coordinates

    // plain / triangle hatch: cap, frame, lid and slider are separate objects of the workshop OBJ, authored closed. The lid
    // hinges at its -X edge (the game's hinge_hatch bone at -0.585, -0.115), the slider and the cap sit on the lid and go
    // with it. There is no ladder: the one in the game's placement guide mesh hung loose from nothing once the hatch was apart
    private static IReadOnlyList<MeshGroupMapping> PlainHatchParts(string obj, int cap, int frame, int lid, int slider,
        string guideDir, string guideObj) => new[]
    {
        new MeshGroupMapping("frame", obj, 0, ObjectIndex: frame, NodeName: "frame"),
        // Closed it is one flat stack: the grated tray (the lid object) with the slider rails and the cap folded over them. Opening
        // makes a 3 m ladder along the wall of the shaft (checked against a recording of the game): the tray swings down on its
        // hinge at the -X edge (bone hinge_hatch at -0.585, -0.115), the slider slides out along it by 0.97 (the cap's hinge, bone
        // hinge_cap, rides at the slider's far end, 0.47, 0.093) and the cap folds out over that end to carry on the ladder.
        // Each part is a child of the one before, so the motions add up
        new MeshGroupMapping("lid", obj, 0, ObjectIndex: lid, NodeName: "lid",
            Anim: new HatchAnim((-0.585f, -0.115f, 0f), (0f, 0f, 1f), 0f, -90f, Start: 0f, End: 0.45f)),
        new MeshGroupMapping("slider", obj, 0, ObjectIndex: slider, NodeName: "slider", ParentNode: "lid",
            Anim: new HatchAnim((0f, 0f, 0f), (0f, 0f, 1f), 0f, 0f, OpenMove: (0.97f, 0f, 0f), Start: 0.35f, End: 0.7f)),
        new MeshGroupMapping("cap", obj, 0, ObjectIndex: cap, NodeName: "cap", ParentNode: "slider",
            Anim: new HatchAnim((0.47f, 0.093f, 0f), (0f, 0f, 1f), 0f, -180f, Start: 0.6f, End: 1f)),
    };

    // armored hatch: the frame and one rigged mesh authored open (lid standing up at the -X edge, lower lid hanging in the
    // shaft with a three section telescoping ladder next to it). Closing swings the lid down (-90 degrees around the hinge
    // line z at x -0.67, y 0.22) and the lower lid up under the frame (+90 degrees at y -0.26) with the ladder on it, the
    // ladder collapses first: the middle section slides up by 0.696 (ladderMid bone -1.1 -> -0.404) and the end section folds
    // back over it (180 degrees at its top end, the ladderEnd bone)
    private static IReadOnlyList<MeshGroupMapping> ArmoredHatchParts(string obj) => new[]
    {
        new MeshGroupMapping("frame", obj, 0, ObjectIndex: 0, NodeName: "frame"),
        new MeshGroupMapping("lid", obj, 0, ObjectIndex: 1, SplitBy: HatchRig.ClassifyArmoredRig, SplitGroup: HatchRig.ArmoredLid,
            NodeName: "lid", Anim: new HatchAnim((-0.67f, 0.22f, 0f), (0f, 0f, 1f), -90f, 0f, Start: 0f, End: 0.5f)),
        new MeshGroupMapping("lower", obj, 0, ObjectIndex: 1, SplitBy: HatchRig.ClassifyArmoredRig, SplitGroup: HatchRig.ArmoredLower,
            NodeName: "lower", Anim: new HatchAnim((-0.67f, -0.26f, 0f), (0f, 0f, 1f), 90f, 0f, Start: 0.2f, End: 0.7f)),
        new MeshGroupMapping("ladder1", obj, 0, ObjectIndex: 1, SplitBy: HatchRig.ClassifyArmoredRig, SplitGroup: HatchRig.ArmoredSec1,
            NodeName: "ladder1", ParentNode: "lower"),
        new MeshGroupMapping("ladder2", obj, 0, ObjectIndex: 1, SplitBy: HatchRig.ClassifyArmoredRig, SplitGroup: HatchRig.ArmoredSec2,
            NodeName: "ladder2", ParentNode: "lower",
            Anim: new HatchAnim((0f, 0f, 0f), (0f, 0f, 1f), 0f, 0f, ClosedMove: (0f, 0.696f, 0f), Start: 0.55f, End: 0.9f)),
        new MeshGroupMapping("ladder3", obj, 0, ObjectIndex: 1, SplitBy: HatchRig.ClassifyArmoredRig, SplitGroup: HatchRig.ArmoredSec3,
            NodeName: "ladder3", ParentNode: "ladder2", Anim: new HatchAnim((-0.497f, -2.22f, 0f), (0f, 0f, 1f), 180f, 0f, Start: 0.7f, End: 1f)),
    };
}
