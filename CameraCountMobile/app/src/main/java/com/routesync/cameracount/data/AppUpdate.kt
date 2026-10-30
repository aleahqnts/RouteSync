package com.routesync.cameracount.data

import com.routesync.cameracount.BuildConfig
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONObject
import java.io.File
import java.util.concurrent.TimeUnit

/**
 * Finds out whether a newer Sentinel has been published, and downloads it.
 *
 * The dashboard answers the check from the GitHub Releases it keeps in memory, so phones
 * sharing a network never run into GitHub's per-address request limit. The APK itself is
 * downloaded from GitHub.
 */
object AppUpdate {

    data class Offer(val version: String, val apkUrl: String)

    private const val FEED_URL = "https://dashboard.routesync.website/app/sentinel-update"

    private val http = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(60, TimeUnit.SECONDS)
        .build()

    /** The offer when the published Sentinel is newer than this build, else null. */
    suspend fun check(): Offer? = withContext(Dispatchers.IO) {
        runCatching {
            http.newCall(Request.Builder().url(FEED_URL).build()).execute().use { res ->
                if (res.code != 200) return@use null
                val json = JSONObject(res.body!!.string())
                val offer = Offer(json.getString("version"), json.getString("apkUrl"))
                offer.takeIf { versionCode(it.version) > BuildConfig.VERSION_CODE }
            }
        }.getOrNull()
    }

    /**
     * Android's version code for a release name, the same rule the build uses: major, minor
     * and patch in two digits each, so 1.13.7 is 11307. Comparing codes rather than names
     * means a preview build, which carries the code of the release it leads to, is never
     * offered the release before it.
     */
    fun versionCode(version: String): Int {
        val (major, minor, patch) = version.split('.').map { it.toInt() }
        return major * 10000 + minor * 100 + patch
    }

    /**
     * Saves the APK to [target], reporting 0 to 1 as it goes.
     *
     * Written under a .part name and renamed once complete, so a file at [target] is always
     * a whole download.
     */
    suspend fun download(url: String, target: File, progress: (Float) -> Unit) = withContext(Dispatchers.IO) {
        val part = File(target.path + ".part")
        http.newCall(Request.Builder().url(url).header("User-Agent", "RouteSync-Sentinel").build()).execute().use { res ->
            check(res.isSuccessful) { "Download failed (${res.code})" }
            val body = res.body!!
            val total = body.contentLength()
            body.byteStream().use { input ->
                part.outputStream().use { out ->
                    val buffer = ByteArray(81920)
                    var done = 0L
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        out.write(buffer, 0, read)
                        done += read
                        if (total > 0) progress(done.toFloat() / total)
                    }
                }
            }
        }
        check(part.renameTo(target)) { "Could not save the download" }
    }
}
