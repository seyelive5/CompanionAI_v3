// MachineSpirit/Knowledge/KnowledgeEntry.cs
// ★ v3.70.0: Data structure for RAG knowledge index
namespace CompanionAI_v3.MachineSpirit.Knowledge
{
    /// <summary>
    /// Single knowledge entry from game Blueprint or Encyclopedia data.
    /// </summary>
    public class KnowledgeEntry
    {
        public string Id;           // Blueprint GUID
        public string Title;        // Display name
        public string Text;         // Full description text (search target)
        public string Category;     // weapon, ability, quest, lore, enemy, item, armor
        public string[] Tokens;     // Pre-tokenized words for BM25
        public float[] Embedding;   // Vector embedding (null until computed)

        public override string ToString() => $"{Title} ({Category})";
    }
}
