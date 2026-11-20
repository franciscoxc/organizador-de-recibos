using Xunit;

// El reloj de los resortes (Resorte.Reloj.cs) es de WPF y no se compila acá: sin él, Animar deja el resorte esperando
// y la prueba lo mueve a mano con Paso. Nunca «quieto»: así todos pasan por Paso.
static partial class Resorte
{
    public static bool Quieto => false;
}

public class ResorteTests
{
    [Fact]
    public void Llega_a_su_destino_y_avisa_una_sola_vez()
    {
        double x = 0;
        var avisos = 0;
        Resorte.Animar(new object(), 10, v => x = v, desde: 0, alLlegar: () => avisos++);
        for (var i = 0; i < 300; i++) Resorte.Paso(1 / 60.0);
        Assert.Equal(10, x);
        Assert.Equal(1, avisos);
    }

    // Lo que cerraba la app sola el 19/11/2026: al llegar, un resorte olvida a otro que se estaba moviendo (el nombre del
    // mes que se va, las burbujas) y que seguía en la lista de ese mismo paso. Los dos llegan en el primer paso y cada
    // uno olvida al otro: no importa cuál vaya primero, el segundo ya no está.
    [Fact]
    public void Al_llegar_uno_puede_olvidar_a_otro_que_se_estaba_moviendo()
    {
        object a = new(), b = new();
        var llegaron = 0;
        Resorte.Animar(a, 1, _ => { }, desde: 1, alLlegar: () => { llegaron++; Resorte.Olvidar(b); });
        Resorte.Animar(b, 1, _ => { }, desde: 1, alLlegar: () => { llegaron++; Resorte.Olvidar(a); });
        Resorte.Paso(1 / 60.0);
        Assert.Equal(1, llegaron);
    }
}
