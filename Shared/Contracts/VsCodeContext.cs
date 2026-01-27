using System.Collections.Generic;

namespace Shared.Contracts
{
    public class VsCodeContext
    {
        public string ActiveDocument { get; set; }
        public (int, int) Selection { get; set; }
        public SolutionNode Solution { get; set; }
    }

    public class SolutionNode
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Type { get; set; } // "solution", "project", "folder", "file"
        public List<SolutionNode> Children { get; set; } = new List<SolutionNode>();
    }
}