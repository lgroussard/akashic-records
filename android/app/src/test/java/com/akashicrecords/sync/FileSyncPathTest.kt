package com.akashicrecords.sync

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class FileSyncPathTest {
    @Test
    fun paths_are_normalised_to_forward_slashes() {
        assertEquals("media/covers/x.jpg", FileSync.normalize("media\\covers\\x.jpg"))
        assertEquals("books/covers/x.jpg", FileSync.normalize("books\\covers\\x.jpg"))
        assertEquals("references/x.jpg", FileSync.normalize("references/x.jpg"))
        assertEquals("media/tickets/x.pdf", FileSync.normalize("/media/tickets/x.pdf"))
        assertEquals("music/Album/titre.mp3", FileSync.normalize("music\\Album\\titre.mp3"))
    }

    @Test
    fun anything_outside_the_synchronised_folders_is_refused() {
        assertNull(FileSync.normalize(null))
        assertNull(FileSync.normalize(""))
        assertNull(FileSync.normalize("..\\..\\ailleurs.txt"))
        assertNull(FileSync.normalize("media\\..\\..\\x"))
        assertNull(FileSync.normalize("media//x.jpg"))
        assertNull(FileSync.normalize("databases/akashic.db"))
        assertNull(FileSync.normalize("C:\\Windows\\win.ini"))
        assertNull(FileSync.normalize("musique\\titre.mp3"))
    }
}
