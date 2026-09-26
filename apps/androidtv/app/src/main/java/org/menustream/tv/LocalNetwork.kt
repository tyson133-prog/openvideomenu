package org.menustream.tv

import android.content.Context
import android.content.pm.PackageManager
import android.os.Build

/**
 * Android 17 (API 37) makes home-network access a runtime permission for apps targeting it: without
 * ACCESS_LOCAL_NETWORK every connection to a private address (Plex on the LAN, the menu server) fails.
 * Apps targeting earlier versions are unaffected, so this only asks when both the device and the app
 * are on 37+.
 */
object LocalNetwork {
    const val PERMISSION = "android.permission.ACCESS_LOCAL_NETWORK"
    private const val ANDROID_17 = 37

    fun mustAsk(context: Context): Boolean =
        Build.VERSION.SDK_INT >= ANDROID_17 &&
            context.applicationInfo.targetSdkVersion >= ANDROID_17 &&
            context.checkSelfPermission(PERMISSION) != PackageManager.PERMISSION_GRANTED
}
