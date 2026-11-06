package io.github.hibrahim365.provance.spring;

import jakarta.servlet.DispatcherType;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;

import java.lang.reflect.Proxy;
import java.security.Principal;
import java.util.HashMap;
import java.util.Map;

/** Minimal servlet request and response for filter tests, without a servlet container. */
final class FakeHttp {

    private FakeHttp() {
    }

    static final class Request {
        String method = "POST";
        String uri = "/api/orders";
        String contextPath = "";
        String query = "token=SECRET";
        String remoteAddr = "10.1.2.3";
        Principal principal;
        final Map<String, String> headers = new HashMap<>(Map.of("User-Agent", "unit-test-agent"));

        HttpServletRequest build() {
            return (HttpServletRequest) Proxy.newProxyInstance(FakeHttp.class.getClassLoader(), new Class<?>[] {HttpServletRequest.class},
                    (proxy, m, args) -> {
                        switch (m.getName()) {
                            case "getMethod": return method;
                            case "getRequestURI": return uri;
                            case "getContextPath": return contextPath;
                            case "getQueryString": return query;
                            case "getRemoteAddr": return remoteAddr;
                            case "getUserPrincipal": return principal;
                            case "getHeader": return headers.get((String) args[0]);
                            case "getDispatcherType": return DispatcherType.REQUEST;
                            case "isAsyncStarted": return false;
                            case "toString": return "FakeRequest " + method + " " + uri;
                            case "hashCode": return System.identityHashCode(proxy);
                            case "equals": return proxy == args[0];
                            default: throw new UnsupportedOperationException(m.getName());
                        }
                    });
        }
    }

    static final class Response {
        int status = 200;
        boolean committed;

        HttpServletResponse build() {
            return (HttpServletResponse) Proxy.newProxyInstance(FakeHttp.class.getClassLoader(), new Class<?>[] {HttpServletResponse.class},
                    (proxy, m, args) -> {
                        switch (m.getName()) {
                            case "getStatus": return status;
                            case "setStatus":
                                status = (Integer) args[0];
                                return null;
                            case "isCommitted": return committed;
                            case "toString": return "FakeResponse " + status;
                            case "hashCode": return System.identityHashCode(proxy);
                            case "equals": return proxy == args[0];
                            default: throw new UnsupportedOperationException(m.getName());
                        }
                    });
        }
    }
}
