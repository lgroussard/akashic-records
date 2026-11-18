package com.akashicrecords.journaux

import org.junit.Assert.assertEquals
import org.junit.Test

// The lines below are written the way the recipes of the journal are.
class ShoppingParserTest {

    private fun shown(ingredients: String, factor: Double = 1.0): List<String> =
        ShoppingParser.parse(ingredients).map { ShoppingParser.display(it.name, it.quantity?.times(factor), it.unit) }

    @Test
    fun readsAmountsUnitsAndRemarks() {
        val text = """
            Pour 3 repas

            200 g de lentilles vertes ou brunes (pas des corail)
            3 gros oignons jaunes
            5 à 6 c. à soupe d'huile d'olive
            1 c. à café de cumin moulu
            Sel, poivre
        """.trimIndent()
        assertEquals(
            listOf(
                "200 g de lentilles vertes ou brunes",
                "3 gros oignons jaunes",
                "6 c. à s. d'huile d'olive",
                "1 c. à c. de cumin moulu",
                "Sel, poivre",
            ),
            shown(text),
        )
        assertEquals(3, ShoppingParser.servingsIn(text))
    }

    @Test
    fun readsRangesFractionsLitresAndDashRemarks() {
        val text = """
            800 g à 1 kg de champignons de Paris
            2 gousses d'ail
            20 g de margarine — remplace le beurre
            1 L de bouillon de légumes — les cubes « classiques » sont parfois au poulet
            3 c. à s. de maïzena + 10 cl d'eau
            1/2 concombre
            65 mL de lait végétal
            2 g d'agar-agar + 1 c. à s. d'eau froide — remplace la gélatine
            250 g de haricots secs — ou 2 boîtes
            Persil plat
        """.trimIndent()
        assertEquals(
            listOf(
                "1 kg de champignons de Paris",
                "2 gousses d'ail",
                "20 g de margarine",
                "1 l de bouillon de légumes",
                "3 c. à s. de maïzena",
                "0,5 concombre",
                "65 ml de lait végétal",
                "2 g d'agar-agar",
                "250 g de haricots secs",
                "Persil plat",
            ),
            shown(text),
        )
    }

    @Test
    fun skipsSectionTitles() {
        val text = """
            Pour 12 demi-fruits

            Crèmes
            100 g de pulpe de passion (2 gros fruits)
            1 gousse de vanille

            Gelée
            1,5 g d'agar-agar

            6 fruits de la passion (coques nettoyées)
        """.trimIndent()
        assertEquals(
            listOf("100 g de pulpe de passion", "1 gousse de vanille", "2 g d'agar-agar", "6 fruits de la passion"),
            shown(text),
        )
    }

    // One line without an amount opening the list is a product, not the title of a part.
    @Test
    fun keepsAProductWithoutAmountAtTheTop() {
        assertEquals(listOf("Huile d'olive", "2 oignons", "Sel"), shown("Huile d'olive\n2 oignons\nSel"))
        assertEquals(
            listOf("200 g de riz", "Coriandre fraîche", "1 citron vert"),
            shown("Pour 2 personnes\n\n200 g de riz\n\nCoriandre fraîche\n1 citron vert"),
        )
    }

    @Test
    fun scales() {
        val text = "200 g de lentilles\n3 gros oignons\n1,5 L de bouillon\nSel"
        assertEquals(listOf("400 g de lentilles", "6 gros oignons", "3 l de bouillon", "Sel"), shown(text, 2.0))
        assertEquals(listOf("65 g de lentilles", "1 gros oignons", "500 ml de bouillon", "Sel"), shown(text, 1.0 / 3))
    }

    @Test
    fun sameProductSameKey() {
        assertEquals(ShoppingParser.key("gousses d'ail", ""), ShoppingParser.key("Gousses  d'ail ", ""))
    }
}
