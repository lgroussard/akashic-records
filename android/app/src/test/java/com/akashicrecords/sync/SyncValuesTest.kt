package com.akashicrecords.sync

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.TreeMap

class SyncValuesTest {
    private val columns = mapOf("Title" to false, "Notes" to false, "Servings" to false, "Margin" to true, "RecueilId" to false)

    private fun row(vararg pairs: Pair<String, Any?>) = TreeMap<String, Any?>().apply { putAll(pairs) }

    @Test
    fun canonical_is_stable_and_order_independent() {
        val a = row("Title" to "Houmous", "Servings" to 4L, "Notes" to "")
        val b = row("Notes" to "", "Servings" to 4L, "Title" to "Houmous")
        assertEquals(SyncValues.canonical(a), SyncValues.canonical(b))
        assertEquals("""{"Notes":"","Servings":4,"Title":"Houmous"}""", SyncValues.canonical(a))
    }

    @Test
    fun canonical_escapes_text_and_keeps_null_apart_from_empty() {
        val quoted = SyncValues.canonical(row("Notes" to "ligne 1\nligne \"2\""))
        assertEquals("""{"Notes":"ligne 1\nligne \"2\""}""", quoted)
        assertNotEquals(SyncValues.canonical(row("Notes" to null)), SyncValues.canonical(row("Notes" to "")))
    }

    @Test
    fun any_change_changes_the_hash() {
        val h = SyncValues.sha(SyncValues.canonical(row("Title" to "Houmous", "Servings" to 4L)))
        assertNotEquals(h, SyncValues.sha(SyncValues.canonical(row("Title" to "Houmous", "Servings" to 5L))))
        assertNotEquals(h, SyncValues.sha(SyncValues.canonical(row("Title" to "houmous", "Servings" to 4L))))
        assertEquals(64, h.length)
    }

    @Test
    fun mutation_id_depends_on_entity_base_and_content() {
        val id = SyncValues.mutationId("Recipe", "abc", 3, "h1")
        assertEquals(id, SyncValues.mutationId("Recipe", "abc", 3, "h1")) // a retry is the same mutation
        assertNotEquals(id, SyncValues.mutationId("Recipe", "abc", 4, "h1"))
        assertNotEquals(id, SyncValues.mutationId("Recipe", "abc", 3, "h2"))
        assertNotEquals(id, SyncValues.mutationId("Recipe", "abc", 3, null)) // a delete
        assertNotEquals(id, SyncValues.mutationId("Poem", "abc", 3, "h1"))
    }

    @Test
    fun project_keeps_known_columns_in_sqlite_types() {
        // What the desktop sends: integers as JSON numbers, a real that happens to be whole, a null, an unknown field.
        val payload = JSONObject("""{"Title":"Houmous","Servings":4,"Margin":19,"RecueilId":null,"FieldFromTheFuture":"x"}""")
        val p = SyncValues.project(payload, columns)
        assertEquals("Houmous", p["Title"])
        assertEquals(4L, p["Servings"])   // Long, like Cursor.getLong
        assertEquals(19.0, p["Margin"])   // Double, like Cursor.getDouble
        assertTrue(p.containsKey("RecueilId"))
        assertNull(p["RecueilId"])
        assertFalse(p.containsKey("FieldFromTheFuture"))
        assertFalse(p.containsKey("Notes")) // not sent: left alone, not blanked
    }

    @Test
    fun a_row_sent_and_read_back_matches_itself() {
        val local = row("Title" to "Houmous", "Notes" to "citron", "Servings" to 4L, "Margin" to 19.0, "RecueilId" to null)
        val back = SyncValues.project(JSONObject(SyncValues.toPayload(local).toString()), columns)
        assertTrue(SyncValues.matches(local, back))
        assertEquals(SyncValues.canonical(local), SyncValues.canonical(back))
    }

    @Test
    fun merge_keeps_both_sides_when_they_touched_different_fields() {
        val ancestor = row("Title" to "Houmous", "Notes" to "", "Servings" to 4L)
        val local = row("Title" to "Houmous", "Notes" to "servir tiede", "Servings" to 4L) // we changed Notes
        val theirs = row("Title" to "Houmous", "Notes" to "", "Servings" to 6L)            // they changed Servings
        val m = SyncValues.merge(local, theirs, ancestor)
        assertEquals(row("Servings" to 6L), m.take)
        assertTrue(m.setAside.isEmpty())
    }

    @Test
    fun merge_sets_their_text_aside_when_both_changed_the_same_field() {
        val ancestor = row("Notes" to "", "Tags" to "")
        val local = row("Notes" to "version telephone", "Tags" to "")
        val theirs = row("Notes" to "version PC", "Tags" to "vegan")
        val m = SyncValues.merge(local, theirs, ancestor)
        assertEquals(row("Tags" to "vegan"), m.take)
        assertEquals(listOf(SyncValues.SetAside("Notes", "version telephone", "version PC")), m.setAside)
    }

    @Test
    fun merge_without_ancestor_prefers_something_over_nothing_and_follows_the_match_column() {
        // Two journal entries for the same day, never synced: ours is the empty one the app created on opening the day.
        val local = row("Title" to "", "Text" to "", "EntryDate" to "2026-11-25T21:14:03", "Tags" to "")
        val theirs = row("Title" to "Mercredi", "Text" to "Mini-PC installe.", "EntryDate" to "2026-11-25T00:00:00", "Tags" to "")
        val m = SyncValues.merge(local, theirs, null, matchColumns = listOf("EntryDate"))
        assertEquals(row("Title" to "Mercredi", "Text" to "Mini-PC installe.", "EntryDate" to "2026-11-25T00:00:00"), m.take)
        assertTrue(m.setAside.isEmpty())

        // Both wrote the day and nobody knows which is newer: the fuller text stays, the other one is set aside
        // (never dropped); nothing of ours is overwritten by their empty title.
        val mine = row("Title" to "Soir", "Text" to "Ecrit ici, longuement.", "EntryDate" to "2026-11-25T21:14:03", "Tags" to "")
        val other = row("Title" to "", "Text" to "Ecrit la-bas.", "EntryDate" to "2026-11-25T00:00:00", "Tags" to "")
        val m2 = SyncValues.merge(mine, other, null, matchColumns = listOf("EntryDate"))
        assertEquals(row("EntryDate" to "2026-11-25T00:00:00"), m2.take)
        assertEquals(listOf(SyncValues.SetAside("Text", "Ecrit ici, longuement.", "Ecrit la-bas.")), m2.setAside)

        // The other way round: theirs is the fuller one, it comes in and ours is what gets set aside.
        val m3 = SyncValues.merge(other, mine, null, matchColumns = listOf("EntryDate"))
        assertEquals("Ecrit ici, longuement.", m3.take["Text"])
        assertEquals(listOf(SyncValues.SetAside("Text", "Ecrit ici, longuement.", "Ecrit la-bas.", lostIsOurs = true)), m3.setAside)
    }

    @Test
    fun matches_compares_only_what_the_payload_carries() {
        val local = row("Title" to "Houmous", "Notes" to "citron", "Servings" to 4L)
        assertTrue(SyncValues.matches(local, row("Title" to "Houmous", "Servings" to 4L)))
        assertFalse(SyncValues.matches(local, row("Title" to "Houmous", "Servings" to 5L)))
        assertFalse(SyncValues.matches(local, row("Title" to "Houmous", "Notes" to null)))
        assertFalse(SyncValues.matches(local, row())) // an empty payload recognises nothing
    }
}
