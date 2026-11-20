# Organizador de Recibos v1.1.2

- **Nuevo: el consolidado.** Además de los recibos separados, cada cliente tiene en la carpeta de los firmados
  `CLIENTE RECIBOS FIRMADOS MM AAAA.pdf`, con todos los recibos que firmaron los empleados, derechos y con
  contraste. Una hoja con dos recibos va una sola vez, y un escaneo, con sus hojas en el orden en que vino. Las
  copias con las iniciales del estudio no van.
  - Si ya existe y tiene a todos, no se toca.
  - Si le falta alguien (una foto que llegó después), se completa: sus hojas quedan como estaban y lo que
    faltaba va al final. Es el único archivo que la app reemplaza, y lo arma aparte: si algo falla, queda el
    que había.
  - Cada hoja lleva escrito, invisible, quién está en ella: con Ctrl+F se busca a cualquiera, y la revisión
    siguiente lo lee sin OCR.
- **Los escaneos que traen su propio texto (Adobe Scan, un escáner) se separan como las fotos.** Antes, de un
  PDF así se guardaban solo los recibos de la hoja que hubo que leer a fondo, y los demás quedaban en verde
  con «Nada para guardar».
- **Lo encontrado solo por el nombre («ilegible, revisar») se guarda igual**, con una marca adentro: en la
  revisión siguiente sigue para revisar y no cuenta como firmado. En la 1.1.1 no se guardaba.

Quien tenga la 1.1.1 va a ver la flecha ofrecerle esta versión.

## Binarios

- `Organizador.de.Recibos.x64.exe`
- `Organizador.de.Recibos.arm.exe`
