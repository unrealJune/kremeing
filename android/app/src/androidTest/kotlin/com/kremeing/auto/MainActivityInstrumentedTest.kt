package com.kremeing.auto

import androidx.test.espresso.Espresso.onView
import androidx.test.espresso.assertion.ViewAssertions.matches
import androidx.test.espresso.matcher.ViewMatchers.isDisplayed
import androidx.test.espresso.matcher.ViewMatchers.withText
import androidx.test.ext.junit.rules.ActivityScenarioRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.rule.GrantPermissionRule
import org.junit.Rule
import org.junit.Test
import org.junit.rules.RuleChain
import org.junit.runner.RunWith

/**
 * Layer 2 — a thin on-emulator smoke test for [MainActivity]. It only proves
 * the activity launches and renders on a real Android runtime (the detailed
 * permission/token/subscribe logic is covered deterministically by the
 * Robolectric suite). Permissions are pre-granted so no system dialog appears,
 * and the activity tolerates a missing Firebase config without crashing.
 */
@RunWith(AndroidJUnit4::class)
class MainActivityInstrumentedTest {

    private val permissions = GrantPermissionRule.grant(
        android.Manifest.permission.ACCESS_COARSE_LOCATION,
        android.Manifest.permission.POST_NOTIFICATIONS,
    )
    private val activity = ActivityScenarioRule(MainActivity::class.java)

    @get:Rule
    val rules: RuleChain = RuleChain.outerRule(permissions).around(activity)

    @Test
    fun launches_and_shows_app_name() {
        onView(withText(R.string.app_name)).check(matches(isDisplayed()))
    }
}
