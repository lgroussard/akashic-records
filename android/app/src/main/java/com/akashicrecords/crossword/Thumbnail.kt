package com.akashicrecords.crossword

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Paint

// A small picture of a grid (game list, widget): black squares, empty squares, and the squares already filled in
// (in the accent colour, so progress shows at a glance without spoiling any letter).
object Thumbnail {
    fun render(puzzle: Puzzle, progress: String, sizePx: Int): Bitmap {
        val rows = puzzle.rows
        val cols = puzzle.cols
        val cell = sizePx / maxOf(rows, cols)
        val bitmap = Bitmap.createBitmap(cell * cols, cell * rows, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(bitmap)
        val paint = Paint(Paint.ANTI_ALIAS_FLAG)
        val lines = progress.split('\n')
        val gap = maxOf(1, cell / 12).toFloat()
        canvas.drawColor(0xFF2A2A3D.toInt())
        for (r in 0 until rows) for (c in 0 until cols) {
            paint.color = when {
                puzzle.isBlack(r, c) -> 0xFF0A0A0E.toInt()
                (lines.getOrNull(r)?.getOrNull(c) ?: '.') in 'A'..'Z' -> 0xFF5B8CFF.toInt()
                else -> 0xFFEDEDF2.toInt()
            }
            canvas.drawRect(c * cell + gap, r * cell + gap, (c + 1) * cell - gap, (r + 1) * cell - gap, paint)
        }
        return bitmap
    }

    // Share of the white squares already filled in (0..1).
    fun filled(progress: String): Float {
        val total = progress.count { it != '#' && it != '\n' }
        return if (total == 0) 0f else progress.count { it in 'A'..'Z' } / total.toFloat()
    }
}
