package com.kremeing.auto.car

import androidx.car.app.CarAppService
import androidx.car.app.Session
import androidx.car.app.validation.HostValidator

/**
 * Entry point the Android Auto host binds to. Declared with the navigation
 * category in the manifest so the app can host a NavigationTemplate and draw its
 * hot-light dashboard on the car's custom Surface, and hand navigation off to
 * the active nav provider.
 */
class KremeingCarAppService : CarAppService() {

    // Demo/dev-friendly: allow all hosts. Production apps should pin to the
    // Google-signed hosts via HostValidator.Builder + allowlist.
    override fun createHostValidator(): HostValidator =
        HostValidator.ALLOW_ALL_HOSTS_VALIDATOR

    override fun onCreateSession(): Session = KremeingSession()
}
