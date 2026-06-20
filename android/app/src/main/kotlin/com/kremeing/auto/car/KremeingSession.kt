package com.kremeing.auto.car

import android.content.Intent
import androidx.car.app.Screen
import androidx.car.app.Session
import com.kremeing.auto.car.screens.HotLightScreen

/** One Android Auto session; opens straight to the lit-stores screen. */
class KremeingSession : Session() {
    override fun onCreateScreen(intent: Intent): Screen = HotLightScreen(carContext)
}
