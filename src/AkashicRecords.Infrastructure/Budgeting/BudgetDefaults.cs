namespace AkashicRecords.Infrastructure.Budgeting;

// One seed of the automatic classifier: a merchant keyword, the category it implies, and that
// category's presentation (colour + optional monthly envelope) used only if the category has to be
// created. Distinctive merchant keywords (SNCF, CARREFOUR, SEMITAN…) carry the load; the generic
// statement verbs (EMIS, PRLV, CARTE) exist only as last-resort TYPE buckets — the classifier keeps
// the LONGEST matching keyword, so a type bucket can never steal a row a merchant keyword also matched.
// Income seeds carry Income = true so they only ever touch money-in rows (Amount > 0), and vice versa.
public sealed record BudgetDefaultRule(string Keyword, string Category, string Color, decimal? Cap, bool Income = false);

// Built-in "class this merchant as X" seeds, applied on import (and on demand) so an imported month
// reads up immediately instead of sitting 100% "Non classé". It never overwrites a manual choice:
// the classifier only fills rows whose category is still NULL. It also never inserts a user rule —
// these stay system seeds, so the user's own rule rail stays clean.
public static class BudgetDefaults
{
    // Colour palette kept in step with the app's dark theme accents.
    public static IReadOnlyList<BudgetDefaultRule> Classification { get; } =
    [
        // Everyday shopping / groceries
        new("CARREFOUR", "Courses", "#4CAF78", null),
        new("KOKO", "Courses", "#4CAF78", null),
        new("SUKO", "Courses", "#4CAF78", null),
        new("MONOPRIX", "Courses", "#4CAF78", null),
        new("LECLERC", "Courses", "#4CAF78", null),
        new("AUCHAN", "Courses", "#4CAF78", null),
        new("INTERMARCHE", "Courses", "#4CAF78", null),

        // Food out
        new("BURGER", "Restaurants", "#E0823C", null),
        new("RESTO", "Restaurants", "#E0823C", null),
        new("PIZZA", "Restaurants", "#E0823C", null),
        new("KEBAB", "Restaurants", "#E0823C", null),

        // Getting around
        new("SNCF", "Transports", "#5B8CFF", null),
        new("VOYAGEURS", "Transports", "#5B8CFF", null),
        new("UBER", "Transports", "#5B8CFF", null),
        new("RATP", "Transports", "#5B8CFF", null),

        // Screens, games, subscriptions to entertainment
        new("STEAM", "Divertissement", "#B06BD6", null),
        new("PATHE", "Divertissement", "#B06BD6", null),
        new("NETFLIX", "Divertissement", "#B06BD6", null),
        new("SPOTIFY", "Divertissement", "#B06BD6", null),
        new("TWITCH", "Divertissement", "#B06BD6", null),
        new("NYX", "Divertissement", "#B06BD6", null),
        new("GOOGLE", "High-Tech", "#59A7C6", null),
        new("PLAYSTATION", "Divertissement", "#B06BD6", null),
        new("MICROSOFT", "High-Tech", "#59A7C6", null),

        // Phone / internet
        new("SFR", "Téléphonie", "#C6A24E", null),
        new("ORANGE", "Téléphonie", "#C6A24E", null),
        new("BOUYG", "Téléphonie", "#C6A24E", null),
        new("FREE", "Téléphonie", "#C6A24E", null),
        new("CANAL", "Téléphonie", "#C6A24E", null),

        // Named merchants seen on the real statements that the buckets above miss.
        // A landlord NAME is not proof the movement is rent — ARTEIS was seeded to Logement and the
        // user rejected it ("that wasn't housing at all"). Seeds must be confident about the CATEGORY,
        // not just recognise the merchant: a wrong category is worse than "Non classé" + one user rule.
        new("PISCINE", "Sport", "#38BDF8", null),
        new("SEMITAN", "Transports", "#5B8CFF", null),
        new("NORAUTO", "Transports", "#5B8CFF", null),
        new("BILLETWEB", "Transports", "#5B8CFF", null),
        new("LOYER", "Logement", "#F2C14E", null),
        new("ALIMENTATION", "Courses", "#4CAF78", null),
        new("MAXICOFFEE", "Restaurants", "#E0823C", null),
        new("AMENDE", "Administrations", "#94A3B8", null),
        new("PARTICIPE", "Donations", "#C084FC", null),
        new("AMAZON", "Shopping", "#F06CA8", null),
        new("FRA PRLV", "Frais bancaires", "#94A3B8", null),
        new("AMNESTY", "Donations", "#C084FC", null),
        new("SECOURS", "Donations", "#C084FC", null),
        new("SOUTIEN", "Donations", "#C084FC", null),

        // Type buckets: when no merchant matched, classify by the NATURE of the operation instead of
        // leaving the row in "Non classé". These keywords are the shortest of the set, so the longest-
        // keyword ladder hands them only the leftovers ("LOYER" still beats "EMIS" on a rent transfer).
        new("CHEQUE", "Chèques", "#A78BFA", null),
        new("PRLV", "Prélèvements", "#FB7185", null),
        new("EMIS", "Virements émis", "#8B9DC3", null),
        new("CARTE", "Carte bancaire", "#7C8BA6", null),

        // Income seeds (Income = true): they only ever touch money-in rows, so a salary/refund line can
        // never be swept into an expense bucket and vice versa. This is what makes the budget readable —
        // money coming in is categorised too, not left to the single undifferentiated "Revenus" total.
        new("SALAIRE", "Revenus", "#4ADE80", null, true),
        new("SOLDE DE", "Revenus", "#4ADE80", null, true),
        new("VIR REC", "Revenus", "#4ADE80", null, true),
        new("VIREMENT REC", "Revenus", "#4ADE80", null, true),
        new("VIR RECU", "Revenus", "#4ADE80", null, true),
        // HelloBank writes "VIR SEPA [INSTANT] RECU /DE …" — the word sits after "SEPA", so only a
        // standalone RECU token matches. The income gate (money-in rows only) keeps it safe.
        new("RECU", "Revenus", "#4ADE80", null, true),
        new("REMUNERATION", "Revenus", "#4ADE80", null, true),
        new("CDE PAIE", "Revenus", "#4ADE80", null, true),
        new("REMBOURSEMENT", "Remboursements", "#3FC5B0", null, true),
        new("REBOURSE", "Remboursements", "#3FC5B0", null, true),
        new("RETOUR", "Remboursements", "#3FC5B0", null, true),
        new("ANNULATION", "Remboursements", "#3FC5B0", null, true),
        new("CADEAU", "Cadeaux", "#E07AB8", null, true),
        new("DON", "Cadeaux", "#E07AB8", null, true),
        new("VENTE", "Ventes", "#C6A24E", null, true),
    ];
}
