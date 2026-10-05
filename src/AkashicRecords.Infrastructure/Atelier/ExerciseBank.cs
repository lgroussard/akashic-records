using AkashicRecords.Domain;

namespace AkashicRecords.Infrastructure.Atelier;

// The Atelier's built-in bank: 32 numbered drills ordered by theme, plus the chrono series.
// The order is the progression — Structure → Valeur → Gesture → Couleur → Composition — and the
// numbering is stable so a session's "7 / 32" always means the same exercise. Text-only source:
// the app works fully offline; Commons enriches the references with thumbnails when online.
public static class ExerciseBank
{
    public static IReadOnlyList<DrawingExercise> All { get; } = Build();
    public static int Count => All.Count;

    public static DrawingExercise? ByNumber(int n) =>
        n >= 1 && n <= All.Count ? All[n - 1] : null;

    // 30-second gesture subjects, taken in order and looped (the chrono never runs dry).
    public static IReadOnlyList<ChronoSubject> ChronoSeries { get; } = new List<ChronoSubject>
    {
        new() { N = 1, Seconds = 30, Subject = "Pichet + 2 fruits" },
        new() { N = 2, Seconds = 30, Subject = "Main gauche, 5 doigts écartés" },
        new() { N = 3, Seconds = 30, Subject = "Chaise en bois, 3 lignes" },
        new() { N = 4, Seconds = 30, Subject = "Tasse vue de dessus" },
        new() { N = 5, Seconds = 30, Subject = "Bouteille haute, ellipse en bas" },
        new() { N = 6, Seconds = 30, Subject = "Livre ouvert, 2 plans" },
    };

    private static List<DrawingExercise> Build() => new()
    {
        // ── Structure ──────────────────────────────────────────────────────────
        new()
        {
            N = 1, Theme = "Structure", Seconds = 300, Title = "Volumes dans 3 plans",
            Steps =
            {
                "Cube en perspective à 1 point, arêtes visibles",
                "Sphère : un cercle + 3 valeurs (clair / moyen / ombre)",
                "Cylindre : ellipses haut et bas, dégradé sur le fût",
            },
            Refs =
            {
                new() { Artist = "Paul Cézanne", Work = "Bouteille, verre et pot", Takeaway = "Décomposer en cylindres et cônes.", Query = "Cezanne bottle glass jug" },
                new() { Artist = "Giorgio Morandi", Work = "Natures mortes", Takeaway = "3 valeurs, rien de plus.", Query = "Morandi still life" },
            },
        },
        new()
        {
            N = 2, Theme = "Structure", Seconds = 300, Title = "Perspective à 2 points",
            Steps =
            {
                "Poser les 2 points de fuite sur l'horizon",
                "Un parallélépipède : 3 faces, fuyantes vers les points",
                "Une 2e boîte plus petite, même horizon",
            },
            Refs =
            {
                new() { Artist = "Edward Hopper", Work = "Intérieurs", Takeaway = "Deux points, volumes nets, rien de superflu.", Query = "Edward Hopper interior" },
            },
        },
        new()
        {
            N = 3, Theme = "Structure", Seconds = 240, Title = "Ellipses et cylindres",
            Steps =
            {
                "5 cylindres de hauteurs différentes",
                "Chaque ellipse : petit axe perpendiculaire au grand",
                "Noms des parties : col, fût, base",
            },
            Refs =
            {
                new() { Artist = "Giorgio Morandi", Work = "Cylindres et parallélépipèdes", Takeaway = "Le nombre de cylindres suffit à tenir la feuille.", Query = "Morandi cylinders" },
            },
        },
        new()
        {
            N = 4, Theme = "Structure", Seconds = 300, Title = "Cones et pyramides",
            Steps =
            {
                "Un cône : ellipse de base + sommet",
                "Une pyramide à base carrée en 2 points",
                "Ombre portée nette, une seule valeur",
            },
            Refs =
            {
                new() { Artist = "Paul Cézanne", Work = "Pyramide de skulls", Takeaway = "L'ombre unique qui soude les volumes.", Query = "Cezanne pyramide skulls" },
            },
        },
        new()
        {
            N = 5, Theme = "Structure", Seconds = 240, Title = "Sphères : la lumière suit",
            Steps =
            {
                "Une sphère par ligne de 4, lumière à 45°",
                "Noircir ombre propre, reflet, ombre portée",
                "Vérifier : 3 valeurs par sphère",
            },
            Refs =
            {
                new() { Artist = "Johannes Vermeer", Work = "La Laitière", Takeaway = "Le point light et le reflet, en 3aplats.", Query = "Vermeer Milkmaid" },
            },
        },
        new()
        {
            N = 6, Theme = "Structure", Seconds = 300, Title = "Assemblage de 3 volumes",
            Steps =
            {
                "Cube + sphère + cylindre, chevauchés",
                "1 seule ombre portée pour les trois",
                "Contrôle : les 3 plans lisibles sans zoom",
            },
            Refs =
            {
                new() { Artist = "Giorgio Morandi", Work = "1956, n° 84", Takeaway = "Le chevauchement qui crée la profondeur.", Query = "Morandi 1956 still life" },
            },
        },

        // ── Valeur ─────────────────────────────────────────────────────────────
        new()
        {
            N = 7, Theme = "Valeur", Seconds = 180, Title = "Échelle 3 valeurs",
            Steps =
            {
                "Trois rectangles : blanc, gris 50 %, noir",
                "Mêmes valeurs sur la sphère du n° 5",
                "Comparer : aucun aplat entre-deux",
            },
            Refs =
            {
                new() { Artist = "Pieter Bruegel l'Ancien", Work = "Chasseurs d'hiver", Takeaway = "Le blanc du papier = valeur 1.", Query = "Bruegel Hunters Snow" },
            },
        },
        new()
        {
            N = 8, Theme = "Valeur", Seconds = 240, Title = "Papier plié",
            Steps =
            {
                "Un carré plié en deux, face pleine",
                "Ombre du pli : la ligne la plus foncée",
                "Reflet : la bande la plus claire",
            },
            Refs =
            {
                new() { Artist = "Juan Gris", Work = "Nature morte au papier", Takeaway = "Le noir du pli, le blanc du reflet.", Query = "Juan Gris paper still life" },
            },
        },
        new()
        {
            N = 9, Theme = "Valeur", Seconds = 240, Title = "Oeuf sur fond gris",
            Steps =
            {
                "Fond = valeur 2 (milieu)",
                "Ombre propre + ombre portée = valeur 3",
                "Reflet en bas = valeur 1",
            },
            Refs =
            {
                new() { Artist = "Chardin", Work = "Le Bénédicité", Takeaway = "L'oeuf, le fond, l'ombre : 3 aplats.", Query = "Chardin Benedictic" },
            },
        },
        new()
        {
            N = 10, Theme = "Valeur", Seconds = 180, Title = "Contraste simultané",
            Steps =
            {
                "Même gris, 2 fonds différents",
                "Le gris paraît plus clair sur le fond sombre",
                "Noter l'écart constaté",
            },
            Refs =
            {
                new() { Artist = "Josef Albers", Work = "Study for Value Study", Takeaway = "Un gris, deux lectures.", Query = "Albers value study" },
            },
        },
        new()
        {
            N = 11, Theme = "Valeur", Seconds = 300, Title = "Tissu froissé",
            Steps =
            {
                "Un drapé, 5 min",
                "3 aplats, pas de modelé",
                "Le pli = trait noir, le bombé = gris",
            },
            Refs =
            {
                new() { Artist = "Léonard de Vinci", Work = "Études de draperies", Takeaway = "Le pli, le bombé, le reflet : trois notes.", Query = "Leonardo drapery study" },
            },
        },
        new()
        {
            N = 12, Theme = "Valeur", Seconds = 240, Title = "Silhouette d'objet",
            Steps =
            {
                "Plein noir au premier plan",
                "Un seul aplat gris derrière",
                "Le blanc du papier = troisième valeur",
            },
            Refs =
            {
                new() { Artist = "Henri Matisse", Work = "Natures mortes au papier découpé", Takeaway = "La valeur par la découpe.", Query = "Matisse still life cut-outs" },
            },
        },

        // ── Gesture ────────────────────────────────────────────────────────────
        new()
        {
            N = 13, Theme = "Gesture", Seconds = 60, Title = "Ligne de contour",
            Steps =
            {
                "Une main, 5 doigts, 60 s",
                "Une ligne continue, stylo non levé",
                "Pas de gomme",
            },
            Refs =
            {
                new() { Artist = "Egon Schiele", Work = "Études de mains", Takeaway = "Le trait unique, sans hésitation.", Query = "Schiele hands" },
            },
        },
        new()
        {
            N = 14, Theme = "Gesture", Seconds = 60, Title = "Silhouette pleine",
            Steps =
            {
                "Un personnage, 3 aplats",
                "Bloc tête + torse + jambes",
                "Le blanc du papier autour suffit",
            },
            Refs =
            {
                new() { Artist = "Matisse", Work = "Etude de nu", Takeaway = "Le bloc, pas le détail.", Query = "Matisse figure study" },
            },
        },
        new()
        {
            N = 15, Theme = "Gesture", Seconds = 120, Title = "Arbre en hiver",
            Steps =
            {
                "Tronc : 1 ligne",
                "2e ordre : 6 branches, 3e ordre : 12",
                "Pas de feuilles",
            },
            Refs =
            {
                new() { Artist = "Pieter Bruegel l'Ancien", Work = "Arbres", Takeaway = "Hiérarchie des branches.", Query = "Bruegel trees winter" },
            },
        },
        new()
        {
            N = 16, Theme = "Gesture", Seconds = 120, Title = "Torse en mouvement",
            Steps =
            {
                "2 blocs : cage thoracique, bassin",
                "Un contre-jour : une seule ombre",
                "Le centre de gravité sous la base",
            },
            Refs =
            {
                new() { Artist = "Michel-Ange", Work = "Études de mains et draperies", Takeaway = "Deux blocs, une ligne d'épaule.", Query = "Michelangelo torso study" },
            },
        },
        new()
        {
            N = 17, Theme = "Gesture", Seconds = 120, Title = "Animaux, 10 traits",
            Steps =
            {
                "Chat : 10 traits, pas 11",
                "Oreilles et dos, rien d'autre",
                "Changer de pose toutes les 2 min",
            },
            Refs =
            {
                new() { Artist = "Jacques-Louis David", Work = "Cheval de Modène", Takeaway = "Le bloc et la ligne du dos.", Query = "David horse" },
            },
        },
        new()
        {
            N = 18, Theme = "Gesture", Seconds = 120, Title = "Paysage en 3 plans",
            Steps =
            {
                "Premier plan noir, fond blanc",
                "Plan du milieu : un seul aplat",
                "Horizon au tiers",
            },
            Refs =
            {
                new() { Artist = "Pieter Bruegel l'Ancien", Work = "Paradies", Takeaway = "Plans successifs, valeurs décroissantes.", Query = "Bruegel landscape horizon" },
            },
        },

        // ── Couleur ────────────────────────────────────────────────────────────
        new()
        {
            N = 19, Theme = "Couleur", Seconds = 240, Title = "Harmonie 3 couleurs",
            Steps =
            {
                "Une dominante, un accent, un neutre",
                "Mélanger avant de poser",
                "Pas de 4e couleur",
            },
            Refs =
            {
                new() { Artist = "Pierre Bonnard", Work = "La Table, 1914", Takeaway = "Le jaune et le rouge sur gris.", Query = "Bonnard table" },
            },
        },
        new()
        {
            N = 20, Theme = "Couleur", Seconds = 240, Title = "Contrastes simultanés",
            Steps =
            {
                "Bleu sur orange, puis orange sur bleu",
                "Le même jaune paraît différent",
                "Relever l'ordre de chaleur",
            },
            Refs =
            {
                new() { Artist = "Josef Albers", Work = "Interaction of Color", Takeaway = "Un jaune, deux lectures.", Query = "Albers Interaction of Color" },
            },
        },
        new()
        {
            N = 21, Theme = "Couleur", Seconds = 300, Title = "Nature morte limitée",
            Steps =
            {
                "Ocre + terre d'ombre + blanc",
                "Lumière unique à 45°",
                "Le noir du fond = valeur 3",
            },
            Refs =
            {
                new() { Artist = "Giorgio Morandi", Work = "Natures mortes", Takeaway = "Le blanc chaud du fond.", Query = "Morandi still life 1946" },
            },
        },
        new()
        {
            N = 22, Theme = "Couleur", Seconds = 180, Title = "Nuancier de valeurs",
            Steps =
            {
                "Une couleur, 5 pas de blanc",
                "Numéroter 1 à 5",
                "Mélange direct, pas de peinture sèche",
            },
            Refs =
            {
                new() { Artist = "Pierre Bonnard", Work = "Études de couleur", Takeaway = "Le blanc chaud et le noir froid.", Query = "Bonnard color study" },
            },
        },
        new()
        {
            N = 23, Theme = "Couleur", Seconds = 300, Title = "Paysage d'hiver",
            Steps =
            {
                "Neige = blanc du papier",
                "Ombres bleutées, aplats",
                "Horizon au tiers",
            },
            Refs =
            {
                new() { Artist = "Pieter Bruegel l'Ancien", Work = "Chasseurs d'hiver", Takeaway = "Le blanc du papier comme valeur.", Query = "Bruegel Hunters Snow" },
            },
        },
        new()
        {
            N = 24, Theme = "Couleur", Seconds = 240, Title = "Portrait, 3 touches",
            Steps =
            {
                "Fond neutre, chair moyenne",
                "1 ombre chaude sous le menton",
                "1 lumière au front",
            },
            Refs =
            {
                new() { Artist = "Johannes Vermeer", Work = "La Laitière", Takeaway = "Point light + ombre chaude.", Query = "Vermeer Milkmaid" },
            },
        },

        // ── Composition ────────────────────────────────────────────────────────
        new()
        {
            N = 25, Theme = "Composition", Seconds = 240, Title = "Tiers",
            Steps =
            {
                "Un objet sur un tiers",
                "Le vide du second plan",
                "Un seul élément au troisième",
            },
            Refs =
            {
                new() { Artist = "Jan van Eyck", Work = "Les Époux Arnolfini", Takeaway = "Les tiers et le remplissage du cadre.", Query = "Arnolfini portrait" },
            },
        },
        new()
        {
            N = 26, Theme = "Composition", Seconds = 240, Title = "Nombre impair",
            Steps =
            {
                "3 objets, pas 4",
                "Le plus proche le plus grand",
                "Espace entre eux, pas de touche",
            },
            Refs =
            {
                new() { Artist = "Giorgio Morandi", Work = "1946, n° 87", Takeaway = "Trois objets, trois valeurs.", Query = "Morandi three objects" },
            },
        },
        new()
        {
            N = 27, Theme = "Composition", Seconds = 300, Title = "Espace négatif",
            Steps =
            {
                "Dessiner les vides d'abord",
                "Puis l'objet, en creux",
                "Le tout en 2 aplats",
            },
            Refs =
            {
                new() { Artist = "Henri Matisse", Work = "Natures mortes au papier découpé", Takeaway = "Le vide comme forme.", Query = "Matisse still life cut-outs" },
            },
        },
        new()
        {
            N = 28, Theme = "Composition", Seconds = 240, Title = "Cadre dans le cadre",
            Steps =
            {
                "Une fenêtre ou arche",
                "Sujet au centre du sous-cadre",
                "Valeur décroissante vers l'extérieur",
            },
            Refs =
            {
                new() { Artist = "Pieter Bruegel l'Ancien", Work = "Procession au Calvaire", Takeaway = "Le sous-cadre qui guide l'oeil.", Query = "Bruegel Calvary" },
            },
        },
        new()
        {
            N = 29, Theme = "Composition", Seconds = 300, Title = "Premier plan écrasant",
            Steps =
            {
                "1 objet au premier plan, 80 % de la hauteur",
                "Un 2e derrière, petit",
                "Fond : le blanc du papier",
            },
            Refs =
            {
                new() { Artist = "Chardin", Work = "La Raie", Takeaway = "Le gros plan qui remplit le cadre.", Query = "Chardin The Ray" },
            },
        },
        new()
        {
            N = 30, Theme = "Composition", Seconds = 240, Title = "Lignes de force",
            Steps =
            {
                "Diagonale 1, diagonale 2",
                "Le sujet sur leur croisement",
                "Aucun élément hors du cadre",
            },
            Refs =
            {
                new() { Artist = "Nicolas Poussin", Work = "Et in Arcadia ego", Takeaway = "Les diagonales, le point d'or.", Query = "Poussin Arcadia" },
            },
        },
        new()
        {
            N = 31, Theme = "Composition", Seconds = 300, Title = "Rythme 1-2-1",
            Steps =
            {
                "Deux blocs serrés, un isolé",
                "Le pair contre l'impair",
                "Répéter la hauteur de 2 en 2",
            },
            Refs =
            {
                new() { Artist = "Poussin", Work = "Paysage avec Pyrame et Thisbé", Takeaway = "Le rythme des plans.", Query = "Poussin Pyramus Thisbe" },
            },
        },
        new()
        {
            N = 32, Theme = "Composition", Seconds = 300, Title = "Synthèse : 4 règles",
            Steps =
            {
                "Tiers, nombre impair, espace négatif, 3 valeurs",
                "Un seul dessin pour tout",
                "Relire la consigne ligne à ligne",
            },
            Refs =
            {
                new() { Artist = "Giorgio Morandi", Work = "1960", Takeaway = "Le bilan : volumes, valeurs, vide.", Query = "Morandi 1960 still life" },
            },
        },
    };
}
