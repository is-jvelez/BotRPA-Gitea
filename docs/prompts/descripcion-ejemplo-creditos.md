# Ejemplo de PARTE C — Bot de validación de créditos

> Esto es **lo único** que escribirías para este bot, pegado al final de [plantilla-bot-rpa.md](plantilla-bot-rpa.md).
> Lo que está entre `[completar: …]` son datos que el requerimiento original no trae. Si los dejas así, Claude los convertirá en preguntas en la Fase 0.
> [ficha-ejemplo-creditos.md](ficha-ejemplo-creditos.md) muestra el tipo de ficha que Claude generaría a partir de este texto.

---

```
Hoy un operador del área de créditos revisa, uno por uno, a los clientes que tienen una solicitud
pendiente y decide si califican a un crédito. Lo hace en la aplicación web de créditos. El bot debe
hacer exactamente lo mismo que el operador y dejar registro de cada decisión.

LA APLICACIÓN
- URL de práctica: [completar: ej. http://localhost:5080, una app mock que levantamos en el compose].
- Inicio de sesión con usuario y contraseña en la pantalla "Iniciar sesión" (campos "Usuario" y
  "Contraseña", botón "Ingresar"). [completar: ¿hay SSO o MFA en el ambiente real?]
- No tenemos API, todo se hace por pantalla. [completar: confirmar]

LA LISTA DE TRABAJO
- Después del login se abre la pantalla "Clientes", con una grilla de columnas: Cédula, Nombre,
  Ingresos mensuales, Relación laboral y Estado.
- El bot procesa solo los clientes con Estado = "Pendiente".
- La grilla muestra 20 filas por página, con botones "Anterior" / "Siguiente".
- Cada cliente se identifica por su Cédula (10 dígitos). No puede haber dos filas con la misma cédula.
- Ingresos mensuales: en dólares, con formato "1.250,00" [completar: confirmar formato exacto].
- Relación laboral: solo puede ser "Dependiente" o "Independiente".

EL RECORRIDO, CLIC A CLIC
1. En la grilla, clic en la fila del cliente (queda resaltada).
2. Clic en el botón "VALIDAR", arriba de la grilla.
3. La app evalúa al cliente y, según el caso, muestra uno de dos botones: "Crédito Aprobado" o
   "Crédito Condicionado". [completar: ¿qué muestra si no califica? ¿nada, un mensaje?]
4. Clic en el botón que apareció. [completar: ¿abre una confirmación? ¿hay que escribir algo?]
5. El Estado del cliente en la grilla cambia a "Aprobado" o "Condicionado".
   [completar: confirmar el texto exacto del estado]

LAS REGLAS (las decide el bot y deben coincidir con lo que muestra la app)
- Si los ingresos mensuales son MAYORES QUE $1.000 (no mayor o igual) y la relación laboral es
  "Dependiente" → crédito APROBADO, con interés del 15 %.
- Si no, si los ingresos son MAYORES QUE $800 y la relación es "Dependiente" → crédito
  CONDICIONADO, con interés del 17 %.
- Se evalúan en ese orden: la primera que se cumple gana.
- Si no se cumple ninguna (ingresos de $800 o menos, o trabajador independiente):
  [completar: ¿se deja en Pendiente sin tocar, o hay que marcarlo como Rechazado?]
- Si la app muestra un botón distinto al que el bot calculó, el bot NO hace clic y deja el caso
  para revisión humana.

LOS CÁLCULOS
- Interés = monto × tasa. [completar: ¿qué monto (solicitado, aprobado)? ¿plazo? ¿la tasa es
  anual? ¿interés simple o compuesto?]
- Redondeo a 2 decimales, mitades hacia arriba. [completar: confirmar]
- Ejemplo resuelto: [completar: un caso calculado por negocio, ej. monto 5.000, plazo 12 meses → …]
- El interés [completar: ¿se escribe en la app, se compara con el que muestra la app, o solo se
  guarda en nuestra base?]

LO YA HECHO
- Un cliente ya procesado tiene Estado "Aprobado" o "Condicionado" y el bot no lo vuelve a tocar.
- Aprobar es irreversible desde la app. [completar: confirmar]

CUANDO ALGO SALE MAL
- Falta la cédula, los ingresos o la relación laboral → no se procesa y queda para revisión humana.
- La app tarda o falla después del clic y no se sabe si se aplicó → revisión humana; nunca se repite
  el clic automáticamente.
- La app no carga o el login falla → la corrida falla y se reintenta en la siguiente ejecución.

CUÁNDO CORRE
- Cada 30 minutos, de lunes a viernes de 08:00 a 18:00, hora de Ecuador (America/Guayaquil).
  [completar: confirmar]

EL RIESGO
- Lo peor es aprobar dos veces o aprobar a quien no corresponde. Dejar un cliente pendiente es grave,
  pero se recupera en la siguiente corrida.

EJEMPLOS (datos ficticios)
| Cédula     | Ingresos | Relación      | Resultado esperado           |
|------------|----------|---------------|------------------------------|
| 0900000001 | 1500,00  | Dependiente   | Aprobado, 15 %               |
| 0900000002 | 1000,00  | Dependiente   | Condicionado, 17 % (no es > 1000) |
| 0900000003 | 900,00   | Dependiente   | Condicionado, 17 %           |
| 0900000004 | 800,00   | Dependiente   | Ninguna regla                |
| 0900000005 | 5000,00  | Independiente | Ninguna regla                |

LO QUE NO HACE
- No desembolsa, no notifica al cliente, no toca el core bancario y no crea solicitudes nuevas.
```
