using NeuroMita.CustomModels;
using UnityEngine;

// Lip sync is driven by matching the game's two mouth shapes (A and O) against whatever the
// replacement model happens to call them. Getting that match wrong is silent: the model still
// loads, the face just never moves. So pin the naming conventions down here.
//
// The case that matters most is the last one in the "real names" block: a pack built on the
// game's own skeleton most naturally reuses the game's own contract names, which carry a
// "Ctrl" prefix. Without stripping it the mouth shapes are never found.

int failures = 0;

void Resolves(string label, string[] names, string expectA, string expectO)
{
    var map = LipSyncBlendShapeResolver.Resolve(names);
    string gotA = map.AIndex >= 0 ? names[map.AIndex] : null;
    string gotO = map.OIndex >= 0 ? names[map.OIndex] : null;

    bool okA = string.Equals(gotA, expectA, StringComparison.Ordinal);
    bool okO = string.Equals(gotO, expectO, StringComparison.Ordinal);
    if (okA && okO) { Console.WriteLine($"  ok   {label}"); return; }

    failures++;
    Console.WriteLine($"  FAIL {label}");
    if (!okA) Console.WriteLine($"         A: expected {expectA ?? "(none)"}, got {gotA ?? "(none)"}");
    if (!okO) Console.WriteLine($"         O: expected {expectO ?? "(none)"}, got {gotO ?? "(none)"}");
}

Console.WriteLine("LipSyncBlendShapeResolver");

// Real naming conventions seen in the wild.
Resolves("game contract names (Ctrl-prefixed)",
    new[] { "CtrlBlink", "CtrlMouthA", "CtrlMouthO", "CtrlMouthM", "CtrlMouthSmile", "CtrlSurprised" },
    "CtrlMouthA", "CtrlMouthO");

Resolves("bare A / O",
    new[] { "Blink", "A", "O", "M", "Smile" }, "A", "O");

Resolves("VRoid Fcl_MTH_*",
    new[] { "Fcl_EYE_Close", "Fcl_MTH_A", "Fcl_MTH_O" }, "Fcl_MTH_A", "Fcl_MTH_O");

Resolves("VRM vrc.v_*",
    new[] { "vrc.v_aa", "vrc.v_oh", "vrc.v_sil" }, "vrc.v_aa", "vrc.v_oh");

Resolves("MouthA / MouthO",
    new[] { "MouthA", "MouthO", "MouthM" }, "MouthA", "MouthO");

Resolves("Mouth_A / Mouth_O",
    new[] { "Mouth_A", "Mouth_O" }, "Mouth_A", "Mouth_O");

Resolves("lowercase phonemes",
    new[] { "ah", "oh" }, "ah", "oh");

// Dropping the Ctrl prefix must not turn every mouth shape into a vowel.
Resolves("CtrlMouthM / CtrlMouthSmile / CtrlMouthWide are not A or O",
    new[] { "CtrlMouthM", "CtrlMouthSmile", "CtrlMouthWide" }, null, null);

Resolves("no mouth shapes at all",
    new[] { "CtrlBlink", "CtrlSurprised" }, null, null);

// Degenerate input must not throw.
Resolves("empty list", Array.Empty<string>(), null, null);
Resolves("unrelated names", new[] { "Smile", "Angry", "Blink" }, null, null);

var nullMap = LipSyncBlendShapeResolver.Resolve((IList<string>)null);
if (nullMap.HasA || nullMap.HasO)
{
    failures++;
    Console.WriteLine("  FAIL null name list produced a match");
}
else Console.WriteLine("  ok   null name list");

// Match through the Mesh overload too -- that is the one the runtime actually calls.
var meshMap = LipSyncBlendShapeResolver.Resolve(new Mesh("CtrlMouthA", "CtrlMouthO"));
if (meshMap.AIndex != 0 || meshMap.OIndex != 1)
{
    failures++;
    Console.WriteLine($"  FAIL Mesh overload: A={meshMap.AIndex} O={meshMap.OIndex}");
}
else Console.WriteLine("  ok   Mesh overload");

Console.WriteLine();
if (failures > 0) throw new InvalidOperationException($"{failures} lip-sync resolver check(s) failed");
Console.WriteLine("all lip-sync resolver checks passed");
