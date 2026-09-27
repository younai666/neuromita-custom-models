// The resolver has one overload that takes a UnityEngine.Mesh. Link the real source file and give
// it just enough of that type to compile, so the test exercises the shipped code rather than a copy.
namespace UnityEngine
{
    public class Mesh
    {
        private readonly string[] _names;
        public Mesh(params string[] names) { _names = names ?? new string[0]; }
        public int blendShapeCount => _names.Length;
        public string GetBlendShapeName(int index) => _names[index];
    }
}
