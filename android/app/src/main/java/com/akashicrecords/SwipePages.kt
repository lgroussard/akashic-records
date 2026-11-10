package com.akashicrecords

import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.wrapContentHeight
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.PagerDefaults
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import kotlin.math.abs

// The one horizontal swipe of the app (calendar months, poetry books and poems, recipe categories and
// recipes, music tabs). Built on HorizontalPager so the page follows the finger from the first pixel and
// settles with the fling — no "nothing happens until release, then an animation" lag. The neighbour on each
// side is composed ahead of time, so its data (a month's dots, a poem) is already loaded when it slides in.
//
// Two-way bound to [page]: chips/buttons that change [page] animate the pager there; a swipe reports the
// new page through [onPageChange] as soon as it passes the middle (not only once it has settled).
@Composable
fun SwipePages(
    count: Int,
    page: Int,
    onPageChange: (Int) -> Unit,
    modifier: Modifier = Modifier,
    key: ((Int) -> Any)? = null,
    verticalAlignment: Alignment.Vertical = Alignment.Top,
    content: @Composable (Int) -> Unit,
) {
    if (count <= 0) return
    val state = rememberPagerState(initialPage = page.coerceIn(0, count - 1)) { count }
    val report by rememberUpdatedState(onPageChange)
    // True while the pager is being moved by code (chip tap, Préc./Suiv.): the pages it crosses on the
    // way are not reported, or the target would be overwritten by an intermediate page.
    var driving by remember { mutableStateOf(false) }

    LaunchedEffect(page, count) {
        val target = page.coerceIn(0, count - 1)
        if (target == state.currentPage) return@LaunchedEffect
        driving = true
        try {
            // Far jumps (e.g. "Aujourd'hui" after many months) land at once instead of scrolling through.
            if (abs(target - state.currentPage) > 2) state.scrollToPage(target) else state.animateScrollToPage(target)
        } finally {
            driving = false
        }
    }

    LaunchedEffect(state) {
        snapshotFlow { state.currentPage }.collect { if (!driving) report(it) }
    }

    HorizontalPager(
        state = state,
        modifier = modifier,
        beyondViewportPageCount = 1,
        key = key,
        verticalAlignment = verticalAlignment,
        // A third of the width (or a quick flick) is enough to turn the page.
        flingBehavior = PagerDefaults.flingBehavior(state, snapPositionalThreshold = 0.3f),
    ) { p -> content(p) }
}

// For a pager placed inside a LazyColumn item: full width, height of its content.
val SwipeInList: Modifier = Modifier.fillMaxWidth().wrapContentHeight()
