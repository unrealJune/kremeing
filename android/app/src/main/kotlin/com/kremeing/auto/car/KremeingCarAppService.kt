package com.kremeing.auto.car

import androidx.car.app.CarAppService
import androidx.car.app.Session
import androidx.car.app.validation.HostValidator

/**
 * Entry point the Android Auto host binds to. Declared with the POI category
 * in the manifest so the app can show place cards and hand navigation off to
 * the active nav provider.
 */
class KremeingCarAppService : CarAppService() {

    // Demo/dev-friendly: allow all hosts. Production apps should pin to the
    // Google-signed hosts via HostValidator.Builder + allowlist.
    override fun createHostValidator(): HostValidator =
        HostValidator.ALLOW_ALL_HOSTS_VALIDATOR

    override fun onCreateSession(): Session = KremeingSession()
}
