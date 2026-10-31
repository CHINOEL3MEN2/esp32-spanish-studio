# ESP32 Spanish Studio

Aplicación de escritorio **Windows (WPF, .NET 10)** que sirve de panel de control para reentrenar en **español** el modelo PLE TinyLM (TinyStories) del proyecto [slvDev/esp32-ai](https://github.com/slvDev/esp32-ai) y ejecutarlo en un **ESP32-S3**.

## ¿Para qué sirve?

Automatiza, desde una sola ventana, todo el flujo:

1. **Datos**: descarga y prepara cuentos en español (`igorshilov/tinystories_spanish`), entrena el tokenizador y tokeniza.
2. **Entrenamiento**: lanza `train.py` sobre el dataset español (GPU CUDA).
3. **Exportar y probar**: exporta `model.bin` (int8) y genera texto de prueba en el PC.
4. **Flashear**: genera `vocab.h`, compila con `arduino-cli`, escribe el modelo en la partición y sube el firmware.
5. **Monitor serie**: lee la salida de la placa (reinicia con RTS para ver el banner).
6. **Pipeline**: encadena todo (con confirmación antes de flashear).

> No traduce un modelo existente: **reentrena** el modelo con datos en español. Solo soporta TinyStories (Barista no se puede reentrenar desde ese repo).

## Resultados obtenidos

| Versión | Vocab | Pasos | Perplejidad | Resultado |
|---|---|---|---|---|
| v1 | 32768 | 15000 | 12.7 | Texto sin sentido |
| es8k | 8192 | 20000 | 8.76 | Español coherente |
| **es8kb** | 8192 | 40000 (4 shards, cuentos ≤1000 caracteres) | **7.33** | Cuentos completos |

En el ESP32-S3 (flash 16 MB, PSRAM octal): **~18 tok/s (50 ms/token)** frente a ~9.5–9.9 tok/s del modelo inglés original (vocabulario menor = cabeza más barata). Con las correcciones de abajo los cuentos **terminan solos**.

Limitaciones: el dataset es traducido automáticamente (muchos nombres siguen en inglés, p. ej. "Lily"); la pantalla solo dibuja ASCII, así que se eliminan tildes y ñ; el contexto es de 256 tokens.

## Correcciones realizadas

- **Crash de la app** (stack overflow): `ListBox.ScrollIntoView` se llamaba dentro de `CollectionChanged`. Ahora se difiere con `Dispatcher.BeginInvoke` y las líneas largas del log se truncan.
- **Cuentos sin final**: el firmware generaba 200 tokens y no paraba en el token de fin. Cambios: `N_GENERATE=256`, `EOT_ID=0` y `break` cuando se muestrea `EOT_ID`. Además `prepare_es.py --max-chars 1000` descarta cuentos que no caben en 256 tokens.

## Requisitos y uso

Este repo contiene solo la app y los parches. Necesita una copia de `slvDev/esp32-ai` (licencia MIT, ver `LICENSE-upstream-esp32-ai`):

1. Copia `research/prepare_es.py` a `research/tinystories/` y aplica `patches/train-export.patch` (añade `--dataset` a `train.py` y `--out` a `export.py`).
2. En el firmware aplica el cambio de EOT descrito arriba y `--prompt` en `tools/generate_vocab.py`.
3. Entorno Python con PyTorch CUDA (usa `.venv\Scripts\python.exe`; `uv sync` puede volver a instalar torch CPU), `arduino-cli` y `esptool`.
4. `dotnet build` en `Esp32SpanishStudio` y ejecuta el `.exe`; indica la ruta del repo y el puerto COM.

Precauciones: la preparación de datos se omite si los archivos ya existen; repetir una etiqueta (tag) sobrescribe el checkpoint; cierra el monitor serie antes de flashear. Más detalles en `Esp32SpanishStudio/README.md`.

## Autoría y tiempo

Esta aplicación la **creó un asistente de IA** (asistente con Copilot SDK en VS Code) a petición de @CHINOEL3MEN2, que dirigió el trabajo y probó la placa. Yo escribí la app, los scripts, la depuración y el reentrenamiento.

**Crear el código de la app llevó unos ~10 minutos** (de las 5:05 a las 5:15, según las marcas de tiempo de los archivos, hasta que compiló y abrió).

El proyecto completo duró ~2,5 horas en total (estimado): ~1,5 h de entrenamiento y esperas de modelo, y el resto depurando y probando (incluido el crash) y flasheando la placa.
