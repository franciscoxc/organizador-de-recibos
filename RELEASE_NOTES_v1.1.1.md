# Organizador de Recibos v1.1.1

Arreglos de una revisión a fondo de la 1.1.0:

- **Lo encontrado solo por el nombre («ilegible, revisar») ya no se guarda corregido.** Ese PDF llevaba el
  CUIL y en la revisión siguiente contaba como firmado sin que nadie lo hubiera mirado. Tampoco cuenta
  como firmado si al lado está la copia con las iniciales del estudio.
- Las palabras del empleador, que encabeza todos los recibos, y las de quien ya está en la hoja por su CUIL
  ya no alcanzan para encontrar a otro empleado por el nombre.
- **Separar nunca pisa un archivo:** si el recibo ya estaba, lo deja y lo avisa. Si los PDF están en una
  unidad de red, pregunta antes de escribir, con cuántos recibos son y en qué carpeta van.
- Dos empleados con el mismo nombre ya no salen juntos en un PDF: cada archivo lleva su CUIL.
- La ventana no se cierra ni se reinicia para actualizar mientras guarda.
- Al pasar de año, la ruta de los clientes cambia solo su año.
- Se arregló un cierre de la app al cambiar de mes.
- Las fotos se leen solo con el OCR de la app: el de Windows leía menos de la mitad.
- **Nuevo:** una carpetita en la barra del mes cambia la carpeta de los clientes. Al pasar por encima dice
  cuál se está usando.

Quien tenga la 1.1.0 va a ver la flecha ofrecerle esta versión.

## Binarios

- `Organizador.de.Recibos.x64.exe`
- `Organizador.de.Recibos.arm.exe`
