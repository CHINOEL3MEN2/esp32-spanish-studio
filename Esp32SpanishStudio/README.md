# ESP32 Spanish Studio

Panel de control WPF (.NET 10) para reentrenar el modelo TinyStories en español y flashearlo al ESP32-S3.

## Ejecutar

```powershell
dotnet run --project windows\Esp32SpanishStudio
# o publicar un .exe:
dotnet publish windows\Esp32SpanishStudio -c Release -r win-x64 --self-contained false
```

## Requisitos

- .NET SDK 10, Python, `uv`, GPU NVIDIA (recomendado), `arduino-cli` con el core `esp32:esp32`, `esptool`.
- La pestaña **Entorno** diagnostica todo; «Instalar / reparar» crea `.venv` e instala PyTorch con CUDA.
- No uses `uv sync`/`uv run` después: pueden reinstalar PyTorch en versión CPU. La app llama directamente a `.venv\Scripts\python.exe`.

## Flujo

1. **Entorno**: elige la carpeta del repo y pulsa *Diagnosticar*.
2. **Datos**: descarga el dataset `igorshilov/tinystories_spanish`, entrena el tokenizador y crea `data/tinystories-es`. «Convertir a ASCII» es recomendable porque la pantalla solo dibuja ASCII (á→a, ñ→n).
3. **Entrenamiento**: pasos, lote, etc. Mantén longitud de secuencia 256 (el firmware está dimensionado para ello). Guarda `runs/ple-<etiqueta>-s<semilla>.pt`.
4. **Exportar y probar**: *Probar generación* en el PC; *Exportar* crea `artifacts/tinystories-es/model.bin`.
5. **Flashear**: regenera vocabulario y prompt, compila, pide confirmación, escribe el modelo (0x110000) y el firmware. Cierra el monitor serie antes.
6. **Monitor serie**: reinicia la placa y compara la huella `fp=` con la esperada.

`Ejecutar pipeline completo` en Inicio encadena todo.

## Límites

- Solo TinyStories; Barista no se puede reentrenar desde este repo.
- El vocabulario máximo es 32768 (una sola partición de modelo de ~15,6 MB).
- El dataset en español es una traducción automática.
