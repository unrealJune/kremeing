package com.kremeing.auto.car

import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Rect
import android.graphics.Typeface
import android.view.Surface
import androidx.car.app.SurfaceCallback
import androidx.car.app.SurfaceContainer
import com.kremeing.auto.logic.HeatBarModel
import com.kremeing.auto.logic.HotLightStatus
import com.kremeing.auto.logic.Interval

/**
 * Draws Kremeing's hot-light "dashboard" directly onto the Android Auto
 * [Surface] via a [Canvas], bypassing the size limits of the Car App templates
 * (rows/images capped at ~480dp). Registered with
 * [androidx.car.app.AppManager.setSurfaceCallback] by a navigation [Screen]
 * ([com.kremeing.auto.car.screens.StoreSurfaceScreen]).
 *
 * This is the spike to confirm projected Android Auto renders a custom surface
 * for our sideloaded app. If it does, the dashboard can grow into a full
 * map+bars view; for now it draws a big title, status, a giant heat bar and the
 * "usually hot" windows — far larger and more glanceable than a template.
 *
 * Surface callbacks arrive on the main thread while the data load runs on a
 * background executor, so all surface state is guarded by [lock].
 */
class CarSurfaceRenderer : SurfaceCallback {

    /** Everything the renderer needs for one frame; rebuilt as data loads. */
    data class Dashboard(
        val title: String,
        val status: HotLightStatus,
        val statusHeadline: String,
        val statusSub: String,
        val usualLine: String,
        val basisLine: String?,
        val intervals: List<Interval>,
        val probabilities: FloatArray?,
        val dayStartMs: Long,
        val dayEndMs: Long,
        val nowMs: Long,
    )

    private val lock = Any()
    private var surface: Surface? = null
    private var surfaceWidth = 0
    private var surfaceHeight = 0
    private val visibleArea = Rect()
    private val stableArea = Rect()
    private var dashboard: Dashboard? = null

    /** Replace the drawn content and repaint immediately. */
    fun setDashboard(d: Dashboard) {
        synchronized(lock) { dashboard = d }
        renderFrame()
    }

    override fun onSurfaceAvailable(surfaceContainer: SurfaceContainer) {
        synchronized(lock) {
            surface = surfaceContainer.surface
            surfaceWidth = surfaceContainer.width
            surfaceHeight = surfaceContainer.height
        }
        renderFrame()
    }

    override fun onVisibleAreaChanged(visibleArea: Rect) {
        synchronized(lock) { this.visibleArea.set(visibleArea) }
        renderFrame()
    }

    override fun onStableAreaChanged(stableArea: Rect) {
        synchronized(lock) { this.stableArea.set(stableArea) }
        renderFrame()
    }

    override fun onSurfaceDestroyed(surfaceContainer: SurfaceContainer) {
        synchronized(lock) {
            // The library contract requires releasing every Surface we receive.
            surface?.release()
            surface = null
        }
    }

    /** Lock the canvas and paint the current [dashboard], guarded against teardown. */
    fun renderFrame() {
        synchronized(lock) {
            val s = surface ?: return
            if (!s.isValid) return
            val canvas = try {
                s.lockCanvas(null)
            } catch (e: IllegalArgumentException) {
                return
            } ?: return
            try {
                draw(canvas)
            } finally {
                try {
                    s.unlockCanvasAndPost(canvas)
                } catch (e: IllegalArgumentException) {
                    // Surface went away mid-frame; the next callback will redraw.
                }
            }
        }
    }

    /** The area guaranteed visible (prefer stable, then visible, then the whole surface). */
    private fun contentRect(): Rect {
        val full = Rect(0, 0, surfaceWidth.coerceAtLeast(1), surfaceHeight.coerceAtLeast(1))
        val area = when {
            !stableArea.isEmpty -> Rect(stableArea)
            !visibleArea.isEmpty -> Rect(visibleArea)
            else -> return full
        }
        return if (area.intersect(full)) area else full
    }

    private fun draw(canvas: Canvas) {
        canvas.drawColor(BG)
        val d = dashboard ?: return
        val area = contentRect()
        val areaH = area.height().toFloat()
        val pad = areaH * 0.06f
        val left = area.left + pad
        val right = area.right - pad
        val contentW = right - left
        var y = area.top + pad

        val text = Paint(Paint.ANTI_ALIAS_FLAG).apply { textAlign = Paint.Align.LEFT }

        // Store name.
        text.typeface = Typeface.DEFAULT_BOLD
        text.color = FG
        text.textSize = areaH * 0.10f
        y += text.textSize
        canvas.drawText(ellipsize(d.title, text, contentW), left, y, text)

        // Big, glanceable status word + a smaller subtitle on the same baseline group.
        text.textSize = areaH * 0.135f
        text.color = statusColor(d.status)
        y += pad * 0.6f + text.textSize
        canvas.drawText(d.statusHeadline, left, y, text)

        text.typeface = Typeface.DEFAULT
        text.color = FG_DIM
        text.textSize = areaH * 0.055f
        y += text.textSize * 1.2f
        canvas.drawText(ellipsize(d.statusSub, text, contentW), left, y, text)

        // The giant heat bar.
        val barTop = y + pad
        val barHeight = areaH * 0.24f
        val barBottom = barTop + barHeight
        drawBar(canvas, left, barTop, right, barBottom, d)

        // Hour ticks under the bar.
        text.color = FG_DIM
        text.textSize = areaH * 0.05f
        text.textAlign = Paint.Align.CENTER
        val labelY = barBottom + text.textSize * 1.3f
        for (h in 0..24 step 6) {
            val x = left + contentW * (h / 24f)
            canvas.drawText(hourLabel(h), x.coerceIn(left, right), labelY, text)
        }
        text.textAlign = Paint.Align.LEFT

        // "Usually hot ..." windows, prominent under the labels.
        text.typeface = Typeface.DEFAULT_BOLD
        text.color = FG
        text.textSize = areaH * 0.07f
        var ty = labelY + text.textSize * 1.4f
        canvas.drawText(ellipsize(d.usualLine, text, contentW), left, ty, text)

        d.basisLine?.let { basis ->
            text.typeface = Typeface.DEFAULT
            text.color = FG_DIM
            text.textSize = areaH * 0.05f
            ty += text.textSize * 1.4f
            canvas.drawText(ellipsize(basis, text, contentW), left, ty, text)
        }
    }

    /** Paint the consolidated bar: predicted ribbon (back) + today's segments + now-marker. */
    private fun drawBar(canvas: Canvas, left: Float, top: Float, right: Float, bottom: Float, d: Dashboard) {
        val bar = HeatBarModel.build(d.intervals, d.probabilities, d.dayStartMs, d.dayEndMs, d.nowMs)
        val w = right - left
        val paint = Paint(Paint.ANTI_ALIAS_FLAG)

        // Bar backdrop (light, so colours pop against the dark dashboard).
        paint.color = Color.parseColor("#F4F2F0")
        canvas.drawRect(left, top, right, bottom, paint)

        bar.ribbon.forEach { slot ->
            val alpha = (20 + slot.probability * 205).toInt().coerceIn(0, 255)
            paint.color = Color.argb(alpha, 0xC7, 0x3E, 0x1D)
            canvas.drawRect(left + (slot.left * w).toFloat(), top, left + ((slot.left + slot.width) * w).toFloat(), bottom, paint)
        }
        bar.segments.forEach { seg ->
            paint.color = colorFor(seg.status)
            paint.alpha = alphaFor(seg.status)
            canvas.drawRect(left + (seg.left * w).toFloat(), top, left + ((seg.left + seg.width) * w).toFloat(), bottom, paint)
        }
        bar.nowFraction?.let { fraction ->
            paint.color = Color.parseColor("#1B1B1B")
            paint.alpha = 255
            paint.strokeWidth = (bottom - top) * 0.05f
            val x = left + (fraction * w).toFloat()
            canvas.drawLine(x, top, x, bottom, paint)
        }
    }

    private fun statusColor(status: HotLightStatus): Int = when (status) {
        HotLightStatus.ON -> Color.parseColor("#FF6B35")
        HotLightStatus.OFF -> Color.parseColor("#9AA0A6")
        HotLightStatus.UNKNOWN -> Color.parseColor("#9AA0A6")
    }

    private fun colorFor(status: HotLightStatus): Int = when (status) {
        HotLightStatus.ON -> Color.parseColor("#C73E1D")
        HotLightStatus.OFF -> Color.parseColor("#FBFAF8")
        HotLightStatus.UNKNOWN -> Color.parseColor("#CACACA")
    }

    private fun alphaFor(status: HotLightStatus): Int = when (status) {
        HotLightStatus.ON -> 255
        HotLightStatus.OFF -> 90
        HotLightStatus.UNKNOWN -> 70
    }

    private fun hourLabel(h: Int): String = when {
        h == 0 || h == 24 -> "12a"
        h < 12 -> "${h}a"
        h == 12 -> "12p"
        else -> "${h - 12}p"
    }

    private fun ellipsize(s: String, paint: Paint, maxWidth: Float): String {
        if (paint.measureText(s) <= maxWidth) return s
        var end = s.length
        while (end > 1 && paint.measureText(s.substring(0, end) + "…") > maxWidth) end--
        return s.substring(0, end) + "…"
    }

    private companion object {
        val BG = Color.parseColor("#101012")
        val FG = Color.parseColor("#FFFFFF")
        val FG_DIM = Color.parseColor("#B8BCC2")
    }
}
