package com.ovmplayer.tv.store

import kotlinx.serialization.builtins.MapSerializer
import kotlinx.serialization.builtins.serializer
import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * App settings. The Plex token is encrypted with an AES key that lives in the Android Keystore and
 * never leaves it; everything else is plain preferences.
 */
class Settings(context: Context) {
    private val prefs = context.getSharedPreferences("ovmplayer", Context.MODE_PRIVATE)

    val clientId: String
        get() = prefs.getString("clientId", null) ?: UUID.randomUUID().toString().also { prefs.edit().putString("clientId", it).apply() }

    var plexToken: String?
        get() = prefs.getString("plexToken", null)?.let { runCatching { Secrets.decrypt(it) }.getOrNull() }
        set(value) = prefs.edit().putString("plexToken", value?.let(Secrets::encrypt)).apply()

    var serverId: String?
        get() = prefs.getString("serverId", null)
        set(value) = prefs.edit().putString("serverId", value).apply()

    /** Asset server base URL, e.g. http://192.168.1.20:8790 */
    var assetServer: String?
        get() = prefs.getString("assetServer", null)
        set(value) = prefs.edit().putString("assetServer", value).apply()

    /** The last server that worked, for starting without plex.tv. Stored encrypted (it holds a token). */
    var lastServer: LastServer?
        get() = prefs.getString("lastServer", null)?.let { runCatching { com.ovmplayer.tv.net.json.decodeFromString(LastServer.serializer(), Secrets.decrypt(it)) }.getOrNull() }
        set(value) = prefs.edit().putString("lastServer", value?.let { Secrets.encrypt(com.ovmplayer.tv.net.json.encodeToString(LastServer.serializer(), it)) }).apply()

    /** Which kind of server this TV signed in to: "plex" or "jellyfin"; null = not chosen yet. */
    var sourceKind: String?
        get() = prefs.getString("sourceKind", null)
        set(value) = prefs.edit().putString("sourceKind", value).apply()

    /** The Jellyfin server and sign-in. Stored encrypted (it holds a token). */
    var jellyfin: JellyfinLogin?
        get() = prefs.getString("jellyfin", null)?.let { runCatching { com.ovmplayer.tv.net.json.decodeFromString(JellyfinLogin.serializer(), Secrets.decrypt(it)) }.getOrNull() }
        set(value) = prefs.edit().putString("jellyfin", value?.let { Secrets.encrypt(com.ovmplayer.tv.net.json.encodeToString(JellyfinLogin.serializer(), it)) }).apply()

    /**
     * Where the viewer stopped each title this app played, with the tracks they had, keyed by server
     * and item ([stopKey]). Servers keep their own resume points, but skip short viewings and often
     * extras, and don't know which soundtrack a menu chose.
     */
    fun stopOf(key: String): Stop? = stops()[key]

    fun rememberStop(key: String, stop: Stop) {
        val all = stops().toMutableMap()
        all[key] = stop
        // Keep the 200 most recent.
        val kept = all.entries.sortedByDescending { it.value.atMs }.take(200).associate { it.key to it.value }
        prefs.edit().putString("stops", com.ovmplayer.tv.net.json.encodeToString(STOPS, kept)).apply()
    }

    fun forgetStop(key: String) {
        val all = stops()
        if (key in all) prefs.edit().putString("stops", com.ovmplayer.tv.net.json.encodeToString(STOPS, all - key)).apply()
    }

    private fun stops(): Map<String, Stop> =
        prefs.getString("stops", null)?.let { runCatching { com.ovmplayer.tv.net.json.decodeFromString(STOPS, it) }.getOrNull() } ?: emptyMap()

    fun signOut() = prefs.edit().remove("plexToken").remove("serverId").remove("lastServer").remove("jellyfin").remove("sourceKind").apply()
}

/**
 * Where the viewer stopped a title, with its soundtrack and subtitles (null = not chosen), and when.
 * [touchedAtMs]: when this app last started playing it, so the server's state changing because of
 * that play isn't mistaken for viewing elsewhere.
 */
@kotlinx.serialization.Serializable
data class Stop(val position: Double, val audio: Int? = null, val subtitle: Int? = null, val atMs: Long, val touchedAtMs: Long = atMs)

private val STOPS = MapSerializer(String.serializer(), Stop.serializer())

@kotlinx.serialization.Serializable
data class JellyfinLogin(val url: String, val serverId: String, val name: String, val userId: String, val token: String)

@kotlinx.serialization.Serializable
data class LastServer(val name: String, val machineId: String, val url: String, val token: String)

private object Secrets {
    private const val ALIAS = "ovmplayer-token-key"

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .build(),
        )
        return gen.generateKey()
    }

    fun encrypt(plain: String): String {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, key()) }
        val data = cipher.iv + cipher.doFinal(plain.toByteArray())
        return Base64.encodeToString(data, Base64.NO_WRAP)
    }

    fun decrypt(stored: String): String {
        val data = Base64.decode(stored, Base64.NO_WRAP)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, data, 0, 12))
        return String(cipher.doFinal(data, 12, data.size - 12))
    }
}
