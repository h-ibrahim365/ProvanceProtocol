package io.github.hibrahim365.provance.json;

import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.lang.reflect.RecordComponent;
import java.math.BigDecimal;
import java.math.BigInteger;
import java.time.temporal.TemporalAccessor;
import java.util.ArrayList;
import java.util.Base64;
import java.util.Calendar;
import java.util.Date;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.UUID;

/**
 * Dependency free serializer for common payload shapes.
 *
 * <ul>
 *   <li>{@code null}, strings, characters, booleans and numbers;</li>
 *   <li>maps (keys converted with {@code toString()}, {@code null} values kept), collections, arrays;</li>
 *   <li>records (components in declaration order) and classes with public getters ({@code getX()},
 *       {@code isX()}); {@code null} properties are omitted;</li>
 *   <li>enums (their name), {@link UUID}, {@code java.time} values and other value types written with
 *       {@code toString()}; {@link Date} as an ISO 8601 instant; {@code byte[]} as Base64; {@link Optional}
 *       as its value or {@code null}.</li>
 * </ul>
 *
 * <p>Write money amounts and large integers as strings: JSON numbers are IEEE 754 doubles once canonicalized.
 */
public final class SimplePayloadSerializer implements PayloadSerializer {

    /** Shared instance. The serializer is stateless and thread safe. */
    public static final SimplePayloadSerializer INSTANCE = new SimplePayloadSerializer();

    private static final int MAX_DEPTH = JsonParser.MAX_DEPTH;

    @Override
    public String toJson(Object payload) {
        StringBuilder sb = new StringBuilder(128);
        write(payload, sb, 0);
        return sb.toString();
    }

    private void write(Object value, StringBuilder sb, int depth) {
        if (depth > MAX_DEPTH) {
            throw new JsonFormatException("Payload is nested more than " + MAX_DEPTH + " levels deep (or contains a cycle).");
        }
        if (value == null) {
            sb.append("null");
        } else if (value instanceof CharSequence || value instanceof Character) {
            JsonCanonicalizer.writeString(value.toString(), sb);
        } else if (value instanceof Boolean) {
            sb.append(value);
        } else if (value instanceof Number) {
            writeNumber((Number) value, sb);
        } else if (value instanceof Enum) {
            JsonCanonicalizer.writeString(((Enum<?>) value).name(), sb);
        } else if (value instanceof Optional) {
            write(((Optional<?>) value).orElse(null), sb, depth);
        } else if (value instanceof UUID || value instanceof TemporalAccessor) {
            JsonCanonicalizer.writeString(value.toString(), sb);
        } else if (value instanceof Date) {
            JsonCanonicalizer.writeString(((Date) value).toInstant().toString(), sb);
        } else if (value instanceof Calendar) {
            JsonCanonicalizer.writeString(((Calendar) value).toInstant().toString(), sb);
        } else if (value instanceof byte[]) {
            JsonCanonicalizer.writeString(Base64.getEncoder().encodeToString((byte[]) value), sb);
        } else if (value instanceof Map) {
            writeMap((Map<?, ?>) value, sb, depth);
        } else if (value instanceof Iterable) {
            sb.append('[');
            boolean first = true;
            for (Object item : (Iterable<?>) value) {
                if (!first) sb.append(',');
                first = false;
                write(item, sb, depth + 1);
            }
            sb.append(']');
        } else if (value.getClass().isArray()) {
            writeArray(value, sb, depth);
        } else if (value.getClass().isRecord()) {
            writeRecord(value, sb, depth);
        } else {
            writeBean(value, sb, depth);
        }
    }

    private static void writeNumber(Number number, StringBuilder sb) {
        if (number instanceof Double || number instanceof Float) {
            double d = number.doubleValue();
            if (!Double.isFinite(d)) {
                throw new JsonFormatException("NaN and Infinity cannot be written to JSON.");
            }
            sb.append(Double.toString(d)); // round-trips exactly; the canonicalizer reformats it
        } else if (number instanceof BigDecimal) {
            sb.append(((BigDecimal) number).toString());
        } else if (number instanceof BigInteger || number instanceof Long || number instanceof Integer
                || number instanceof Short || number instanceof Byte) {
            sb.append(number.toString());
        } else {
            double d = number.doubleValue();
            if (!Double.isFinite(d)) {
                throw new JsonFormatException("NaN and Infinity cannot be written to JSON.");
            }
            sb.append(Double.toString(d));
        }
    }

    private void writeMap(Map<?, ?> map, StringBuilder sb, int depth) {
        sb.append('{');
        boolean first = true;
        for (Map.Entry<?, ?> e : map.entrySet()) {
            if (e.getKey() == null) {
                throw new JsonFormatException("Map keys cannot be null.");
            }
            if (!first) sb.append(',');
            first = false;
            String key = e.getKey() instanceof Enum ? ((Enum<?>) e.getKey()).name() : e.getKey().toString();
            JsonCanonicalizer.writeString(key, sb);
            sb.append(':');
            write(e.getValue(), sb, depth + 1);
        }
        sb.append('}');
    }

    private void writeArray(Object array, StringBuilder sb, int depth) {
        int length = java.lang.reflect.Array.getLength(array);
        sb.append('[');
        for (int i = 0; i < length; i++) {
            if (i > 0) sb.append(',');
            write(java.lang.reflect.Array.get(array, i), sb, depth + 1);
        }
        sb.append(']');
    }

    private void writeRecord(Object record, StringBuilder sb, int depth) {
        sb.append('{');
        boolean first = true;
        for (RecordComponent component : record.getClass().getRecordComponents()) {
            Object v = invoke(component.getAccessor(), record);
            if (v == null) {
                continue;
            }
            if (!first) sb.append(',');
            first = false;
            JsonCanonicalizer.writeString(component.getName(), sb);
            sb.append(':');
            write(v, sb, depth + 1);
        }
        sb.append('}');
    }

    private void writeBean(Object bean, StringBuilder sb, int depth) {
        List<Method> getters = new ArrayList<>();
        for (Method m : bean.getClass().getMethods()) {
            if (Modifier.isStatic(m.getModifiers()) || m.getParameterCount() != 0 || m.getDeclaringClass() == Object.class) {
                continue;
            }
            if (propertyName(m) != null) {
                getters.add(m);
            }
        }
        if (getters.isEmpty()) {
            throw new JsonFormatException("Type " + bean.getClass().getName()
                    + " has no public getter and cannot be serialized. Use a record, a map, or plug in your own PayloadSerializer.");
        }
        getters.sort((a, b) -> propertyName(a).compareTo(propertyName(b)));
        sb.append('{');
        boolean first = true;
        for (Method getter : getters) {
            Object v = invoke(getter, bean);
            if (v == null) {
                continue;
            }
            if (!first) sb.append(',');
            first = false;
            JsonCanonicalizer.writeString(propertyName(getter), sb);
            sb.append(':');
            write(v, sb, depth + 1);
        }
        sb.append('}');
    }

    private static String propertyName(Method m) {
        String name = m.getName();
        if (name.startsWith("get") && name.length() > 3 && m.getReturnType() != void.class) {
            return decapitalize(name.substring(3));
        }
        if (name.startsWith("is") && name.length() > 2 && (m.getReturnType() == boolean.class || m.getReturnType() == Boolean.class)) {
            return decapitalize(name.substring(2));
        }
        return null;
    }

    private static String decapitalize(String name) {
        // JavaBeans rule: "URL" stays "URL", "Name" becomes "name".
        if (name.length() > 1 && Character.isUpperCase(name.charAt(0)) && Character.isUpperCase(name.charAt(1))) {
            return name;
        }
        return Character.toLowerCase(name.charAt(0)) + name.substring(1);
    }

    private static Object invoke(Method method, Object target) {
        try {
            if (!method.canAccess(target)) {
                method.setAccessible(true);
            }
            return method.invoke(target);
        } catch (IllegalAccessException | RuntimeException e) {
            throw new JsonFormatException("Cannot read " + method.getName() + " of " + target.getClass().getName() + ".", e);
        } catch (InvocationTargetException e) {
            throw new JsonFormatException("Reading " + method.getName() + " of " + target.getClass().getName() + " failed.", e.getCause());
        }
    }
}
