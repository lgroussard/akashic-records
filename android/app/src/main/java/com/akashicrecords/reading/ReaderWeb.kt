package com.akashicrecords.reading

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Color
import android.graphics.Rect
import android.os.Handler
import android.os.Looper
import android.view.ActionMode
import android.view.Menu
import android.view.MenuItem
import android.view.View
import android.webkit.JavascriptInterface
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayInputStream
import java.util.zip.ZipFile

// What the page tells the reader screen. Called on the main thread.
interface ReaderEvents {
    fun onPage(page: Int, pages: Int)
    fun onEdge(next: Boolean)            // swipe/tap past the last (next) or first page of the chapter
    fun onToggleChrome()
    fun onSelection(start: Int, end: Int, text: String) // "Surligner" chosen on a selection
    fun onHighlightTapped(id: Int)
}

// Reader look, applied by CSS (see ReaderWeb.css). fontScale in %, font: "book" | "serif" | "sans".
data class ReaderLook(val fontScale: Int = 100, val font: String = "serif", val theme: String = "dark") {
    val background: Int get() = when (theme) { "light" -> 0xFFFBFAF7.toInt(); "sepia" -> 0xFFF4ECD8.toInt(); else -> 0xFF14141C.toInt() }
    val foreground: String get() = when (theme) { "light" -> "#1E1E24"; "sepia" -> "#5B4636"; else -> "#DCDCE6" }
    val backgroundCss: String get() = when (theme) { "light" -> "#FBFAF7"; "sepia" -> "#F4ECD8"; else -> "#14141C" }
}

// WebView showing one chapter of an epub at a time, paginated in CSS columns (one column = one screen). Resources
// (chapters, images, CSS, fonts) are served straight from the zip under https://book.akashic/<zip path>; the
// chapter documents get the reader's CSS + script injected (pagination, swipe, taps, highlights).
@SuppressLint("SetJavaScriptEnabled", "ViewConstructor")
class ReaderWeb(context: Context, private val epub: Epub, private val events: ReaderEvents) : WebView(context) {
    private val zip = ZipFile(epub.file)
    private val chapterPaths = epub.chapters.toHashSet()
    private val main = Handler(Looper.getMainLooper())
    var look = ReaderLook()
        private set

    init {
        settings.javaScriptEnabled = true
        settings.allowFileAccess = false
        settings.cacheMode = WebSettings.LOAD_NO_CACHE
        isVerticalScrollBarEnabled = false
        isHorizontalScrollBarEnabled = false
        overScrollMode = View.OVER_SCROLL_NEVER
        setBackgroundColor(Color.TRANSPARENT)
        addJavascriptInterface(Bridge(), "Reader")
        // Page errors and console output go to logcat (tag AkashicReader), to diagnose odd books.
        webChromeClient = object : android.webkit.WebChromeClient() {
            override fun onConsoleMessage(m: android.webkit.ConsoleMessage): Boolean {
                android.util.Log.d("AkashicReader", "${m.message()} (${m.sourceId()}:${m.lineNumber()})")
                return true
            }
        }
        webViewClient = object : WebViewClient() {
            override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse? {
                val url = request.url
                if (url.host != HOST) return WebResourceResponse("text/plain", "utf-8", ByteArrayInputStream(ByteArray(0)))
                val path = Epub.resolve("", url.encodedPath.orEmpty()).substringBefore('#')
                val entry = zip.getEntry(path) ?: return WebResourceResponse("text/plain", "utf-8", 404, "Not found", emptyMap(), ByteArrayInputStream(ByteArray(0)))
                val bytes = zip.getInputStream(entry).use { it.readBytes() }
                val mime = mimeOf(path)
                val body = if (path in chapterPaths) inject(bytes.decodeToString()).encodeToByteArray() else bytes
                return WebResourceResponse(mime, "utf-8", ByteArrayInputStream(body))
            }

            // Links inside the book (notes, cross-references) open the target chapter; outside links do nothing.
            override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                val url = request.url
                if (url.host != HOST) return true
                val path = Epub.resolve("", url.encodedPath.orEmpty())
                val chapter = epub.chapters.indexOf(path.substringBefore('#'))
                if (chapter >= 0) main.post { linkHandler?.invoke(chapter) }
                return true
            }
        }
    }

    var linkHandler: ((Int) -> Unit)? = null

    fun showChapter(index: Int, progression: Double, highlights: List<BookHighlightMark>) {
        pending = progression to highlights
        loadUrl("https://$HOST/" + epub.chapters[index].split('/').joinToString("/") { Uri_encode(it) })
    }

    // Applied to the page now and to every chapter loaded later.
    fun applyLook(newLook: ReaderLook) {
        look = newLook
        setBackgroundColor(look.background)
        evaluateJavascript("window.AK && AK.setLook(${lookJson()})", null)
    }

    fun goToProgression(p: Double) = evaluateJavascript("window.AK && AK.goProgress($p)", null)
    fun nextPage() = evaluateJavascript("window.AK && AK.step(1)", null)
    fun previousPage() = evaluateJavascript("window.AK && AK.step(-1)", null)
    fun scrollToHighlight(id: Int) = evaluateJavascript("window.AK && AK.showHighlight($id)", null)
    fun setHighlights(list: List<BookHighlightMark>) = evaluateJavascript("window.AK && AK.setHighlights(${marksJson(list)})", null)

    fun release() {
        runCatching { zip.close() }
        destroy()
    }

    // Progression + highlights to apply once the chapter has laid out (AK.ready calls back for them).
    private var pending: Pair<Double, List<BookHighlightMark>>? = null

    private inner class Bridge {
        @JavascriptInterface fun ready(): String {
            val (p, marks) = pending ?: (0.0 to emptyList())
            pending = null
            return JSONObject().put("progress", p).put("look", JSONObject(lookJson())).put("marks", JSONArray(marksJson(marks))).toString()
        }
        @JavascriptInterface fun onPage(page: Int, pages: Int) = main.post { events.onPage(page, pages) }.let { }
        @JavascriptInterface fun onEdge(next: Boolean) = main.post { events.onEdge(next) }.let { }
        @JavascriptInterface fun onToggleChrome() = main.post { events.onToggleChrome() }.let { }
        @JavascriptInterface fun onSelection(start: Int, end: Int, text: String) = main.post { events.onSelection(start, end, text) }.let { }
        @JavascriptInterface fun onHighlightTapped(id: Int) = main.post { events.onHighlightTapped(id) }.let { }
    }

    // Adds "Surligner" to the text-selection toolbar.
    override fun startActionMode(callback: ActionMode.Callback, type: Int): ActionMode? = super.startActionMode(wrap(callback), type)
    override fun startActionMode(callback: ActionMode.Callback): ActionMode? = super.startActionMode(wrap(callback))

    private fun wrap(original: ActionMode.Callback): ActionMode.Callback = object : ActionMode.Callback2() {
        // Added first and marked "always": the WebView's items are "if room", so ours stays in the visible toolbar
        // instead of the "⋮" overflow.
        override fun onCreateActionMode(mode: ActionMode, menu: Menu): Boolean {
            menu.add(Menu.NONE, HIGHLIGHT_ITEM, 0, "Surligner").setShowAsAction(MenuItem.SHOW_AS_ACTION_ALWAYS)
            return original.onCreateActionMode(mode, menu)
        }
        override fun onPrepareActionMode(mode: ActionMode, menu: Menu) = original.onPrepareActionMode(mode, menu)
        override fun onActionItemClicked(mode: ActionMode, item: MenuItem): Boolean {
            if (item.itemId != HIGHLIGHT_ITEM) return original.onActionItemClicked(mode, item)
            evaluateJavascript("window.AK && AK.captureSelection()", null)
            mode.finish()
            return true
        }
        override fun onDestroyActionMode(mode: ActionMode) = original.onDestroyActionMode(mode)
        override fun onGetContentRect(mode: ActionMode, view: View, outRect: Rect) {
            if (original is ActionMode.Callback2) original.onGetContentRect(mode, view, outRect) else super.onGetContentRect(mode, view, outRect)
        }
    }

    private fun lookJson() = JSONObject()
        .put("scale", look.fontScale).put("font", look.font).put("fg", look.foreground).put("bg", look.backgroundCss).toString()

    private fun marksJson(list: List<BookHighlightMark>) =
        JSONArray(list.map { JSONObject().put("id", it.id).put("s", it.start).put("e", it.end) }).toString()

    // Reader CSS + script right before </head> (or at the start of <body>). XHTML is parsed as XML, hence the CDATA.
    private fun inject(doc: String): String {
        // The viewport tag pins the page to the screen width (without it the WebView reports a far wider page).
        val payload = "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no\"/>" +
            "<style id=\"ak-base\">$BASE_CSS</style><style id=\"ak-look\"></style>" +
            "<script>//<![CDATA[\n$SCRIPT\n//]]></script>"
        val head = Regex("</head\\s*>", RegexOption.IGNORE_CASE).find(doc)
        return if (head != null) doc.replaceRange(head.range.first, head.range.first, payload)
        else Regex("<body\\b[^>]*>", RegexOption.IGNORE_CASE).find(doc)?.let { doc.replaceRange(it.range.last + 1, it.range.last + 1, payload) }
            ?: (payload + doc)
    }

    companion object {
        const val HOST = "book.akashic"
        private const val HIGHLIGHT_ITEM = 0x4B41

        private fun Uri_encode(seg: String) = android.net.Uri.encode(seg)

        fun mimeOf(path: String) = when (path.substringAfterLast('.').lowercase()) {
            "xhtml", "xht" -> "application/xhtml+xml"
            "html", "htm" -> "text/html"
            "css" -> "text/css"
            "jpg", "jpeg" -> "image/jpeg"
            "png" -> "image/png"
            "gif" -> "image/gif"
            "svg" -> "image/svg+xml"
            "webp" -> "image/webp"
            "ttf" -> "font/ttf"
            "otf" -> "font/otf"
            "woff" -> "font/woff"
            "woff2" -> "font/woff2"
            "js" -> "text/javascript"
            else -> "application/octet-stream"
        }

        private const val BASE_CSS =
            // !important: the reading CSS clears every background in the book (see layout), highlights excepted.
            "mark.ak-hl{background:rgba(255,200,60,.45)!important;color:inherit;border-radius:2px}" +
                "::selection{background:rgba(120,160,255,.45)}"

        // Pagination, swipe, taps and highlights. Pages are CSS columns as wide as the screen; the document scrolls
        // sideways by whole screens. Offsets of highlights are character positions in the body's text.
        private val SCRIPT = """
(function(){
  if (window.AK) return;
  var AK = window.AK = {}, M = 24, T = 30, look = null, marks = [], ready = false;
  var root = function(){ return document.scrollingElement || document.documentElement; };
  // Page width, fixed at each layout (screen width in CSS pixels); every page is exactly that wide.
  var PW = 0;
  var W = function(){ return PW || document.documentElement.clientWidth; };
  function layout(){
    PW = 0; var w = document.documentElement.clientWidth, h = window.innerHeight; PW = w;
    var css = 'html{overflow:hidden!important;height:'+h+'px!important;margin:0!important;padding:'+T+'px 0 0 0!important;box-sizing:border-box!important;background:'+look.bg+'!important}'
      + 'body{margin:0 '+M+'px!important;padding:0!important;height:'+(h-2*T)+'px!important;width:auto!important;max-width:none!important;'
      + 'column-width:'+(w-2*M)+'px!important;column-gap:'+(2*M)+'px!important;column-fill:auto!important;'
      + 'background:'+look.bg+'!important;color:'+look.fg+'!important;font-size:'+look.scale+'%!important;line-height:1.55!important;'
      + (look.font==='book' ? '' : 'font-family:'+(look.font==='sans'?'sans-serif':'Georgia,serif')+'!important;') + '}'
      + 'body *:not(mark){color:inherit!important;background-color:transparent!important;max-width:100%!important}'
      + (look.font==='book' ? '' : 'p,div,span,li,blockquote{font-family:inherit!important}')
      + 'img,svg,video{max-width:100%!important;max-height:'+(h-2*T)+'px!important;object-fit:contain;break-inside:avoid}'
      + 'a{color:inherit!important;text-decoration:underline dotted}';
    document.getElementById('ak-look').textContent = css;
  }
  AK.pages = function(){ return Math.max(1, Math.round((root().scrollWidth - 1) / W())); };
  AK.page = function(){ return Math.round(root().scrollLeft / W()); };
  function report(){ Reader.onPage(AK.page(), AK.pages()); }
  var anim = 0;
  function scrollTo(x, animate){
    cancelAnimationFrame(anim);
    var r = root(), from = r.scrollLeft;
    if (!animate) { r.scrollLeft = x; report(); return; }
    var t0 = performance.now(), d = 180;
    (function frame(now){
      var k = Math.min(1, (now - t0) / d); k = 1 - Math.pow(1 - k, 3);
      r.scrollLeft = from + (x - from) * k;
      if (k < 1) anim = requestAnimationFrame(frame); else report();
    })(t0);
  }
  AK.go = function(p, animate){ p = Math.max(0, Math.min(AK.pages() - 1, p)); scrollTo(p * W(), animate); };
  AK.step = function(d){
    var p = AK.page() + d;
    if (p < 0) { Reader.onEdge(false); return; }
    if (p > AK.pages() - 1) { Reader.onEdge(true); return; }
    AK.go(p, true);
  };
  AK.goProgress = function(f){ AK.go(Math.round(f * (AK.pages() - 1)), false); };
  AK.setLook = function(l){
    var f = AK.pages() > 1 ? AK.page() / (AK.pages() - 1) : 0;
    look = l; layout(); setTimeout(function(){ AK.goProgress(f); }, 60);
  };

  // --- Swipe that follows the finger; taps on the side thirds turn pages, the middle shows/hides the controls ---
  var sx = 0, sy = 0, st = 0, t0 = 0, drag = false, moved = false;
  function selecting(){ var s = window.getSelection(); return s && !s.isCollapsed; }
  document.addEventListener('touchstart', function(e){
    if (e.touches.length !== 1) return;
    sx = e.touches[0].clientX; sy = e.touches[0].clientY; st = root().scrollLeft; t0 = Date.now(); drag = false; moved = false;
    cancelAnimationFrame(anim);
  }, {passive: true});
  document.addEventListener('touchmove', function(e){
    if (e.touches.length !== 1) return;
    var dx = e.touches[0].clientX - sx, dy = e.touches[0].clientY - sy;
    if (Math.abs(dx) > 8 || Math.abs(dy) > 8) moved = true;
    if (!drag && Math.abs(dx) > 10 && Math.abs(dx) > Math.abs(dy) && !selecting()) drag = true;
    if (drag) { root().scrollLeft = st - dx; e.preventDefault(); }
  }, {passive: false});
  document.addEventListener('touchend', function(e){
    var x = (e.changedTouches[0] || {}).clientX || sx;
    if (drag) {
      var dx = x - sx, fast = Math.abs(dx) / Math.max(1, Date.now() - t0) > 0.5;
      var base = Math.round(st / W()), dir = (dx < -W() * 0.25 || (fast && dx < -20)) ? 1 : (dx > W() * 0.25 || (fast && dx > 20)) ? -1 : 0;
      var target = base + dir;
      if (target < 0) { AK.go(0, true); Reader.onEdge(false); }
      else if (target > AK.pages() - 1) { AK.go(AK.pages() - 1, true); Reader.onEdge(true); }
      else AK.go(target, true);
      drag = false; return;
    }
    if (moved || selecting() || Date.now() - t0 > 400) return;
    var mark = e.target && e.target.closest ? e.target.closest('mark.ak-hl') : null;
    if (mark) { Reader.onHighlightTapped(parseInt(mark.getAttribute('data-h'))); e.preventDefault(); return; }
    if (e.target && e.target.closest && e.target.closest('a[href]')) return;
    if (x > W() * 2 / 3) AK.step(1); else if (x < W() / 3) AK.step(-1); else Reader.onToggleChrome();
    e.preventDefault();
  }, {passive: false});

  // --- Highlights: [start, end) character offsets in document.body's text ---
  function offsetOf(node, off){
    var r = document.createRange(); r.setStart(document.body, 0); r.setEnd(node, off); return r.toString().length;
  }
  AK.captureSelection = function(){
    var s = window.getSelection(); if (!s || s.isCollapsed || !s.rangeCount) return;
    var r = s.getRangeAt(0), a = offsetOf(r.startContainer, r.startOffset), b = offsetOf(r.endContainer, r.endOffset);
    var text = r.toString(); s.removeAllRanges();
    if (b > a && text.trim().length) Reader.onSelection(a, b, text);
  };
  function unwrap(){
    var list = document.querySelectorAll('mark.ak-hl');
    for (var i = 0; i < list.length; i++) { var m = list[i], p = m.parentNode; while (m.firstChild) p.insertBefore(m.firstChild, m); p.removeChild(m); p.normalize(); }
  }
  function wrap(h){
    var w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT), n, acc = 0, nodes = [];
    while ((n = w.nextNode())) { var len = n.nodeValue.length; if (acc + len > h.s && acc < h.e) nodes.push([n, acc]); acc += len; if (acc >= h.e) break; }
    nodes.forEach(function(it){
      var node = it[0], at = it[1], a = Math.max(0, h.s - at), b = Math.min(node.nodeValue.length, h.e - at);
      if (b <= a || !node.nodeValue.substring(a, b).trim().length) return;
      var mid = a > 0 ? node.splitText(a) : node; if (b - a < mid.nodeValue.length) mid.splitText(b - a);
      var m = document.createElement('mark'); m.className = 'ak-hl'; m.setAttribute('data-h', h.id);
      mid.parentNode.insertBefore(m, mid); m.appendChild(mid);
    });
  }
  AK.setHighlights = function(list){ marks = list; unwrap(); list.forEach(wrap); };
  AK.showHighlight = function(id){
    var m = document.querySelector('mark.ak-hl[data-h="'+id+'"]'); if (!m) return;
    AK.go(Math.floor((m.getBoundingClientRect().left + root().scrollLeft) / W()), false);
  };

  function start(){
    if (ready) return; ready = true;
    var init = JSON.parse(Reader.ready());
    look = init.look; layout(); AK.setHighlights(init.marks);
    var settle = function(){ AK.goProgress(init.progress); };
    setTimeout(settle, 30); window.addEventListener('load', function(){ setTimeout(settle, 30); });
    window.addEventListener('resize', function(){ var f = AK.pages() > 1 ? AK.page() / (AK.pages() - 1) : 0; layout(); setTimeout(function(){ AK.goProgress(f); }, 60); });
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
})();
"""
    }
}

data class BookHighlightMark(val id: Int, val start: Int, val end: Int)
