using System.Reflection;

var type = Assembly.GetExecutingAssembly().GetType("NeuroMita.CustomModels.RendererSlotMatcher");
if (type == null) throw new InvalidOperationException("RendererSlotMatcher is missing");
var method = type.GetMethod("Score", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
if (method == null) throw new InvalidOperationException("RendererSlotMatcher.Score is missing");

int Score(string part, string renderer, string mesh = null, string[] materials = null) =>
    (int)method.Invoke(null, new object[] { part, renderer, mesh, materials });

void Positive(string part, string renderer, string mesh = null, string[] materials = null) {
    var score = Score(part, renderer, mesh, materials);
    if (score <= 0) throw new InvalidOperationException($"Expected match: {part} -> {renderer} (score {score})");
}
void Negative(string part, string renderer, string mesh = null) {
    var score = Score(part, renderer, mesh);
    if (score > 0) throw new InvalidOperationException($"Unexpected match: {part} -> {renderer} (score {score})");
}

Positive("Head", "Head");
Positive("Hair", "HairRenderer");
Positive("Jacket", "UpperClothingSlot");
Positive("Skirt", "SkirtSlot");
Positive("Footwear", "ShoesSlot");
Positive("Stockings", "HosierySlot");
Positive("Skin", "BodySlot");
Positive("FaceAccessory", "AccessorySlot");
Positive("LowerGarment", "LowerGarmentRenderer", "SkirtMesh");
Positive("Sandals", "GenericRenderer", "GenericMesh", new[] { "FootwearMaterial" });
Negative("Footwear", "BodySlot", "ShoesMesh");
Negative("UnclassifiedPart", "GenericRenderer", "GenericMesh");
Positive("UnclassifiedPart", "UnclassifiedPartRenderer");
Console.WriteLine("RendererSlotMatcher behavior checks passed.");
