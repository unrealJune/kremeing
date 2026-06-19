package com.kremeing.auto.api

import com.kremeing.auto.logic.HotLightStatus
import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpHandler
import com.sun.net.httpserver.HttpServer
import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.BeforeEach
import org.junit.jupiter.api.Test
import java.net.InetSocketAddress

/**
 * Exercises [KremeingApiClient] end-to-end against a real local HTTP server,
 * pinning the exact requests it makes (method, path, query, body) and how it
 * decodes responses via the shared `:logic` codec. This is the UX/client side
 * of the contract the F# backend tests pin on the server side.
 */
class KremeingApiClientTest {

    private lateinit var server: HttpServer
    private lateinit var baseUrl: String
    private val requests = mutableListOf<RecordedRequest>()

    data class RecordedRequest(
        val method: String,
        val path: String,
        val query: String?,
        val body: String,
    )

    private var responder: (RecordedRequest) -> Pair<Int, String> = { 200 to "{}" }

    @BeforeEach
    fun setUp() {
        server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/", object : HttpHandler {
            override fun handle(exchange: HttpExchange) {
                val body = exchange.requestBody.readBytes().toString(Charsets.UTF_8)
                val recorded = RecordedRequest(
                    method = exchange.requestMethod,
                    path = exchange.requestURI.path,
                    query = exchange.requestURI.query,
                    body = body,
                )
                requests.add(recorded)
                val (status, response) = responder(recorded)
                val bytes = response.toByteArray(Charsets.UTF_8)
                exchange.sendResponseHeaders(status, bytes.size.toLong())
                exchange.responseBody.use { it.write(bytes) }
            }
        })
        server.start()
        baseUrl = "http://127.0.0.1:${server.address.port}"
    }

    @AfterEach
    fun tearDown() {
        server.stop(0)
    }

    @Test
    fun `nearbyStores issues a GET with coordinates and parses the list`() {
        responder = { _ ->
            200 to """
                {"stores":[
                  {"id":1,"name":"KK One","address":"1 Main","latitude":47.6,"longitude":-122.3,
                   "distanceMiles":0.4,"currentStatus":"on"},
                  {"id":2,"name":"KK Two","address":"2 Main","latitude":47.7,"longitude":-122.4,
                   "distanceMiles":1.0,"currentStatus":"off"}
                ]}
            """.trimIndent()
        }

        val stores = KremeingApiClient(baseUrl).nearbyStores(47.6, -122.3, 25.0)

        assertEquals(2, stores.size)
        assertEquals(HotLightStatus.ON, stores[0].status)
        val req = requests.single()
        assertEquals("GET", req.method)
        assertEquals("/stores/nearby", req.path)
        val query = req.query!!
        assertTrue(query.contains("latitude=47.6"), query)
        assertTrue(query.contains("longitude=-122.3"), query)
        assertTrue(query.contains("radiusMiles=25.0"), query)
    }

    @Test
    fun `subscribeDevice POSTs the registration and parses the id`() {
        responder = { _ -> 201 to """{"id":42,"radiusMiles":25.0}""" }

        val resp = KremeingApiClient(baseUrl).subscribeDevice("tok-1", 47.6, -122.3, 25.0)

        assertEquals(42L, resp.id)
        val req = requests.single()
        assertEquals("POST", req.method)
        assertEquals("/device-subscriptions", req.path)
        assertTrue(req.body.contains("\"token\":\"tok-1\""), req.body)
        assertTrue(req.body.contains("\"platform\":\"android\""), req.body)
        assertTrue(req.body.contains("\"radiusMiles\":25.0"), req.body)
    }

    @Test
    fun `unsubscribeDevice issues a DELETE with the token`() {
        responder = { _ -> 200 to "{}" }

        KremeingApiClient(baseUrl).unsubscribeDevice("tok-1")

        val req = requests.single()
        assertEquals("DELETE", req.method)
        assertEquals("/device-subscriptions", req.path)
        assertTrue(req.body.contains("\"token\":\"tok-1\""), req.body)
    }

    @Test
    fun `non-2xx responses raise KremeingApiException carrying the status`() {
        responder = { _ -> 503 to """{"error":{"code":"push_disabled"}}""" }

        val ex = assertThrows(KremeingApiException::class.java) {
            KremeingApiClient(baseUrl).subscribeDevice("tok-1", 47.6, -122.3, 25.0)
        }
        assertEquals(503, ex.statusCode)
        assertTrue(ex.responseBody.contains("push_disabled"))
    }
}
