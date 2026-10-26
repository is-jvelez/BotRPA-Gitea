# Ficha de ejemplo — Bot de validación de créditos

> Ejemplo de la ficha (formato PARTE B de [plantilla-bot-rpa.md](plantilla-bot-rpa.md)) que **Claude genera en la Fase 0**; tú no la escribes.
> Lo que escribes tú es la descripción en texto libre: ver [descripcion-ejemplo-creditos.md](descripcion-ejemplo-creditos.md).
> Los `<PENDIENTE: ...>` son preguntas reales que el requerimiento original no responde; hay que cerrarlas antes de la Fase 1.

### B.1 Identidad
- `job_name`: `validar-creditos`
- Objetivo: por cada cliente de la lista, decidir si califica a crédito Aprobado o Condicionado, calcular el interés y dejarlo validado en la app, como hoy lo hace un operador.
- Proceso que reemplaza: operador entra a la web, selecciona cliente, pulsa VALIDAR y, según el caso, pulsa "Crédito Aprobado" o "Crédito Condicionado".
- Dueño de las reglas: `<PENDIENTE: definir responsable>`

### B.2 Sistema destino
- URL práctica: `<PENDIENTE>` (recomendado: mock web en el compose con pantalla de lista de clientes, botón VALIDAR y botones de resultado).
- Autenticación: `<PENDIENTE: usuario/clave? SSO?>` → `TARGET_USER` / `TARGET_PASSWORD`.
- ¿Hay API? `<PENDIENTE>`

### B.3 Fuente de ítems
- Lista de clientes: `<PENDIENTE: ¿la grilla de la propia web, un Excel, una tabla SQL?>`
- `item_key`: `<PENDIENTE: cédula / código de cliente>` (enmascarado en logs).
- `idempotency_scope`: `<PENDIENTE: ¿un crédito por cliente para siempre, por solicitud o por día?>`

### B.4 Datos a leer por ítem

| Campo | Origen | Tipo | Obligatorio | Ejemplo | ¿PII? |
|---|---|---|---|---|---|
| Identificador cliente | `<PENDIENTE>` | string | sí | 0912345678 | sí |
| Nombre | `<PENDIENTE>` | string | no | Cliente Prueba | sí |
| Ingresos | `<PENDIENTE>` | decimal (USD) | sí | 1200.00 | sí |
| Relación laboral | `<PENDIENTE>` | enum | sí | Dependiente | no |

- `<PENDIENTE: ¿ingresos mensuales? ¿brutos o netos?>`
- `<PENDIENTE: valores posibles exactos de "relación laboral" en la app>`
- Dato obligatorio faltante → `NeedsReview` (propuesto).

### B.5 Reglas de decisión (primera que aplica gana) — `RuleVersion = v1`

| # | Condición | Resultado | Tasa | Botón esperado |
|---|---|---|---|---|
| 1 | ingresos **> 1000** y relación = Dependiente | Aprobado | 15 % | "Crédito Aprobado" |
| 2 | ingresos **> 800** y relación = Dependiente | Condicionado | 17 % | "Crédito Condicionado" |
| 3 | Ninguna aplica | `<PENDIENTE: ¿Rechazado con acción en la app, o Skipped sin tocar nada?>` | — | — |

Consecuencias que hay que confirmar:
- Ingresos = 1000.00 exacto → **Condicionado** (por usar `>`). `<PENDIENTE: confirmar>`
- Ingresos = 800.00 exacto → **ninguna regla**. `<PENDIENTE: confirmar>`
- Independiente con ingresos 5000 → **ninguna regla**. `<PENDIENTE: confirmar>`

### B.6 Cálculos
- Tasa: 15 % (Aprobado) / 17 % (Condicionado).
- `<PENDIENTE: ¿sobre qué monto? ¿plazo? ¿interés simple o compuesto? ¿tasa anual o mensual?>`
- `<PENDIENTE: decimales y redondeo (ej. 2 decimales, MidpointRounding.AwayFromZero)>`
- `<PENDIENTE: ¿el bot ingresa el interés en la app, lo compara con el que muestra la app, o solo lo registra?>`
- Ejemplo resuelto: `<PENDIENTE: un caso calculado a mano por negocio>`

### B.7 Acciones en el destino
1. Buscar y seleccionar al cliente en la lista.
2. Clic en **VALIDAR**.
3. Esperar a que aparezca "Crédito Aprobado" o "Crédito Condicionado".
4. `<PENDIENTE: ¿el bot debe hacer clic en ese botón? ¿qué pasa al hacerlo (confirmación, formulario, pantalla nueva)?>`

- Verificación del éxito: `<PENDIENTE: ¿mensaje, cambio de estado en la grilla?>`
- Cómo se ve un cliente ya procesado (check-then-act): `<PENDIENTE>`
- Si el botón que muestra la app no coincide con la decisión del bot (ej. el bot calcula Aprobado y la app ofrece Condicionado): **`NeedsReview`, sin hacer clic** (propuesto).
- La app no muestra ningún botón tras VALIDAR: `<PENDIENTE: ¿equivale a "no califica" o es un error?>`

### B.8 Salida y registro
- `bots.credit_decisions`: `item_key`, ingresos y relación leídos, regla aplicada, `rule_version`, resultado, tasa, interés calculado, botón mostrado por la app, acción ejecutada, estado, `job_run_id`, `attempt`, timestamps.
- Evidencia: captura antes y después de cada clic; retención `<PENDIENTE>`.

### B.9 Programación
- Cron y zona horaria: `<PENDIENTE>` (America/Guayaquil propuesto).
- Ventana permitida: `<PENDIENTE: ¿solo horario laboral?>`

### B.10 Criterio de riesgo
- **Duplicar es lo peor** (aprobar dos veces o aprobar a quien no corresponde); omitir es grave pero recuperable en la siguiente corrida.
- Acción irreversible: clic en "Crédito Aprobado"/"Crédito Condicionado" `<PENDIENTE: confirmar si se puede deshacer>`.

### B.11 Casos de prueba de negocio

| Caso | Ingresos | Relación | Resultado esperado | Tasa |
|---|---|---|---|---|
| Aprobado claro | 1500.00 | Dependiente | Aprobado | 15 % |
| Límite superior +0.01 | 1000.01 | Dependiente | Aprobado | 15 % |
| Límite superior exacto | 1000.00 | Dependiente | Condicionado | 17 % |
| Condicionado claro | 900.00 | Dependiente | Condicionado | 17 % |
| Límite inferior +0.01 | 800.01 | Dependiente | Condicionado | 17 % |
| Límite inferior exacto | 800.00 | Dependiente | Ninguna regla (`<PENDIENTE>`) | — |
| Independiente alto | 5000.00 | Independiente | Ninguna regla (`<PENDIENTE>`) | — |
| Ingreso faltante | — | Dependiente | NeedsReview | — |
| App muestra botón distinto | 1500.00 | Dependiente | NeedsReview, sin clic | — |
| Ya procesado en la app | 1500.00 | Dependiente | Verified, sin clic | — |

Más un caso de interés calculado por cada resultado cuando B.6 esté cerrado.

### B.12 Fuera de alcance
- Desembolso, notificaciones al cliente, integración con core bancario, colas, Azure.
