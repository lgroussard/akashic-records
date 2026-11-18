namespace AkashicRecords.Domain;

// A personal recipe book entry - not a food-management system, just the user's own recipe archive.
public sealed class Recipe
{
    public int Id { get; set; }
    public required string Title { get; set; }
    // Single user-defined classification (e.g. "Ramen", "Dessert") - distinct from free-form Tags.
    public string Category { get; set; } = string.Empty;
    public string Ingredients { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    // Comma-separated - simple free-form tagging, no fixed taxonomy.
    public string Tags { get; set; } = string.Empty;
    public string? CoverImagePath { get; set; }
    // How many portions (one person, one meal) the recipe gives as written; 0 = not said. The phone's
    // shopping list scales the ingredients from it.
    public int Servings { get; set; }
    public DateTime CreatedAt { get; set; }
}
