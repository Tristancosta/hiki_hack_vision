namespace HikiHackVision.Features.Cut;

/// <summary>Resultado da leitura do OSD. <see cref="Date"/> é null se a data não for legível.</summary>
public record OsdReading(DateOnly? Date, TimeOnly Time, int Distance);

/// <summary>Posição do texto do OSD no quadro: grade monoespaçada e célula onde começa o horário.</summary>
public record OsdLayout(double Origin, double Pitch, int Top, int Height, int TimeCell);

/// <summary>
/// Lê data/hora do OSD da Hikvision por comparação com modelos da fonte bitmap (branca com contorno preto).
/// Mais confiável que OCR genérico: o Tesseract erra dígitos com confiança (ex.: 18 → 16) sobre fundos claros.
/// <para><see cref="Calibrate"/>: acha o texto sem saber onde está (máscara dos traços → componentes →
/// grade monoespaçada → busca do padrão dd:dd:dd). Funciona bem com fundo escuro/médio.</para>
/// <para><see cref="ReadWithLayout"/>: com a grade já conhecida (o OSD é fixo no vídeo), compara cada célula
/// verificando traço claro + contorno escuro e ignorando o fundo; funciona também com céu branco atrás.</para>
/// </summary>
public static class OsdGlyphReader
{
    private const int Cols = 7, Rows = 10;
    private static readonly char[] Digits = "0123456789".ToCharArray();

    /// <param name="gray">Pixels em escala de cinza (1 byte por pixel) da região do canto superior esquerdo.</param>
    /// <param name="frameHeight">Altura do quadro inteiro (a fonte do OSD escala com a resolução).</param>
    public static (OsdReading Reading, OsdLayout Layout)? Calibrate(byte[] gray, int width, int height, int frameHeight)
    {
        var mask = BuildMask(gray, width, height, frameHeight);
        var comps = Components(mask, width, height);

        var cands = comps.Where(c =>
        {
            int w = c.X1 - c.X0 + 1, h = c.Y1 - c.Y0 + 1;
            return h >= frameHeight / 50.0 && h <= frameHeight / 24.0 && w >= h * 0.5 && w <= h * 0.85;
        }).ToList();

        // Linhas candidatas: glifos com mesmo topo e base (±1px), das mais populosas para as menos
        var rows = cands.GroupBy(c => (c.Y0, c.Y1)).OrderByDescending(g => g.Count()).Take(4);
        foreach (var row in rows)
        {
            var members = cands.Where(c => Math.Abs(c.Y0 - row.Key.Y0) <= 1 && Math.Abs(c.Y1 - row.Key.Y1) <= 1)
                .OrderBy(c => c.X0).ToList();
            if (members.Count < 4) continue;

            var grid = FitGrid(members, row.Key.Y0, row.Key.Y1 - row.Key.Y0 + 1);
            if (grid == null) continue;

            var count = (int)((width - grid.Origin) / grid.Pitch);
            var cells = new Dictionary<char, int>?[count];
            var found = Decode(i =>
            {
                if (i < 0 || i >= count) return null;
                return cells[i] ??= MaskScores(grid, i, mask, width, height);
            }, 0, count - 8, MaskAccept);
            if (found != null) return (found.Value.Reading, grid with { TimeCell = found.Value.TimeCell });
        }
        return null;
    }

    /// <summary>Lê o OSD numa posição já conhecida (mesmo vídeo). Robusto a fundo claro.</summary>
    public static OsdReading? ReadWithLayout(byte[] gray, int width, int height, OsdLayout layout)
    {
        var cache = new Dictionary<int, Dictionary<char, int>?>();
        var found = Decode(i => cache.TryGetValue(i, out var s) ? s : cache[i] = ContrastScores(layout, i, gray, width, height),
            layout.TimeCell, layout.TimeCell, ContrastAccept);
        return found?.Reading;
    }

    private readonly record struct Comp(int X0, int Y0, int X1, int Y1);

    // Critérios de aceite de um dígito: distância ao melhor modelo e folga para o 2º colocado
    private static bool MaskAccept(int dist, int margin) => (dist <= 10 && margin >= 3) || (dist <= 16 && margin >= 4);
    private static bool ContrastAccept(int dist, int margin) => (dist <= 8 && margin >= 3) || (dist <= 14 && margin >= 5);

    /// <summary>
    /// Procura "HH:MM:SS" começando nas células [first, last] e, se houver, "AAAA-MM-DD" 15 células antes.
    /// Cada dígito é restrito aos valores possíveis na posição (ex.: dezena da hora só 0-2).
    /// </summary>
    private static (OsdReading Reading, int TimeCell)? Decode(
        Func<int, Dictionary<char, int>?> cellScores, int first, int last, Func<int, int, bool> accept)
    {
        int? Digit(int i, string allowed)
        {
            var s = cellScores(i);
            if (s == null) return null;
            var ranked = allowed.Select(c => (C: c, D: s[c])).OrderBy(x => x.D).ToList();
            var margin = ranked.Count > 1 ? ranked[1].D - ranked[0].D : 99;
            return accept(ranked[0].D, margin) ? ranked[0].C - '0' : null;
        }
        // Separador: basta parecer mais com ele do que com qualquer dígito (a posição já é fixada pelo padrão)
        bool Sep(int i, char c) => cellScores(i) is { } s && s[c] < Digits.Min(d => s[d]);
        int Dist(int i) => cellScores(i)!.Where(kv => char.IsDigit(kv.Key)).Min(kv => kv.Value);

        const string Any = "0123456789", To2 = "012", To5 = "012345";
        (OsdReading Reading, int TimeCell)? best = null;
        for (var i = Math.Max(0, first); i <= last; i++)
        {
            if (!Sep(i + 2, ':') || !Sep(i + 5, ':')) continue;
            if (Digit(i, To2) is not { } h1 || Digit(i + 1, Any) is not { } h2
                || Digit(i + 3, To5) is not { } m1 || Digit(i + 4, Any) is not { } m2
                || Digit(i + 6, To5) is not { } s1 || Digit(i + 7, Any) is not { } s2) continue;

            int hh = h1 * 10 + h2, mm = m1 * 10 + m2, ss = s1 * 10 + s2;
            if (hh > 23) continue;
            var dist = new[] { i, i + 1, i + 3, i + 4, i + 6, i + 7 }.Sum(Dist);

            DateOnly? date = null;
            var d = i - 15;
            if (Sep(d + 4, '-') && Sep(d + 7, '-')
                && Digit(d, "2") is { } y1 && Digit(d + 1, Any) is { } y2 && Digit(d + 2, Any) is { } y3 && Digit(d + 3, Any) is { } y4
                && Digit(d + 5, "01") is { } mo1 && Digit(d + 6, Any) is { } mo2
                && Digit(d + 8, "0123") is { } d1 && Digit(d + 9, Any) is { } d2)
            {
                int yyyy = y1 * 1000 + y2 * 100 + y3 * 10 + y4, mo = mo1 * 10 + mo2, dd = d1 * 10 + d2;
                if (mo is >= 1 and <= 12 && dd >= 1 && dd <= DateTime.DaysInMonth(yyyy, mo))
                    date = new DateOnly(yyyy, mo, dd);
            }

            if (best == null || dist < best.Value.Reading.Distance)
                best = (new OsdReading(date, new TimeOnly(hh, mm, ss), dist), i);
        }
        return best;
    }

    /// <summary>Traço de caractere: pixel claro com contorno escuro dos dois lados (horizontal ou vertical).</summary>
    private static bool[] BuildMask(byte[] px, int w, int h, int frameHeight)
    {
        var reach = Math.Max(3, (int)Math.Round(6 * frameHeight / 720.0));
        bool Dark(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && px[y * w + x] < 90;

        var mask = new bool[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (px[y * w + x] <= 160) continue;
            bool l = false, r = false, u = false, d = false;
            for (var k = 1; k <= reach; k++)
            {
                l |= Dark(x - k, y); r |= Dark(x + k, y); u |= Dark(x, y - k); d |= Dark(x, y + k);
            }
            mask[y * w + x] = (l && r) || (u && d);
        }
        return mask;
    }

    private static List<Comp> Components(bool[] mask, int w, int h)
    {
        var seen = new bool[mask.Length];
        var comps = new List<Comp>();
        var stack = new Stack<int>();
        for (var i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || seen[i]) continue;
            seen[i] = true; stack.Push(i);
            int x0 = w, y0 = h, x1 = 0, y1 = 0;
            while (stack.Count > 0)
            {
                var k = stack.Pop(); int x = k % w, y = k / w;
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                    var kk = yy * w + xx;
                    if (mask[kk] && !seen[kk]) { seen[kk] = true; stack.Push(kk); }
                }
            }
            comps.Add(new Comp(x0, y0, x1, y1));
        }
        return comps;
    }

    /// <summary>Encontra passo e origem da grade monoespaçada que alinham o maior número de glifos.</summary>
    private static OsdLayout? FitGrid(List<Comp> members, int top, int height)
    {
        // A fonte tem células de 8x10 "pixels" (7 de glifo + 1 de espaço): passo ≈ 0,8 × altura
        var pitch0 = height * 0.8;

        // Fase da grade que alinha mais glifos (início do glifo fica 0..2px após o início da célula)
        (int Score, double Phase) best = (0, 0);
        for (var phase = 0.0; phase < pitch0; phase += 0.25)
        {
            var score = members.Count(m => Deviation(m.X0, phase, pitch0) is >= -1 and <= 3);
            if (score > best.Score) best = (score, phase);
        }
        if (best.Score < 4) return null;

        var aligned = members.Where(m => Deviation(m.X0, best.Phase, pitch0) is >= -1 and <= 3)
            .Select(m => (K: Math.Round((m.X0 - best.Phase) / pitch0), X: (double)m.X0))
            .ToList();

        // Regressão x0 = a + b·k refina o passo (erros de 0,1px acumulam ao longo de 20+ células)
        double pitch = pitch0, a = best.Phase;
        var kMean = aligned.Average(p => p.K);
        var sKK = aligned.Sum(p => (p.K - kMean) * (p.K - kMean));
        if (aligned.Count >= 5 && sKK > 0)
        {
            var xMean = aligned.Average(p => p.X);
            var b = aligned.Sum(p => (p.K - kMean) * (p.X - xMean)) / sKK;
            if (Math.Abs(b - pitch0) < pitch0 * 0.05) { pitch = b; a = xMean - b * kMean; }
            else a = xMean - pitch0 * kMean;
        }

        // Origem = início real da célula: os glifos mais à esquerda da própria célula (resíduo mínimo)
        var residuals = aligned.Select(p => p.X - (a + pitch * p.K)).Order().ToList();
        var origin = a + residuals[residuals.Count / 10];
        origin -= Math.Floor(origin / pitch) * pitch; // primeira célula a partir de x >= 0
        return new OsdLayout(origin, pitch, top, height, 0);

        static double Deviation(int x, double phase, double p)
        {
            var off = (x - phase) / p;
            return (off - Math.Round(off)) * p;
        }
    }

    /// <summary>Distância (Hamming na máscara) da célula a cada modelo, tolerando deslocamento de ±1px.</summary>
    private static Dictionary<char, int> MaskScores(OsdLayout g, int index, bool[] mask, int w, int h)
    {
        var scores = Templates.ToDictionary(t => t.Key, _ => int.MaxValue);
        var bits = new bool[Cols * Rows];
        for (var sy = -1; sy <= 1; sy++)
        for (var sx = -1; sx <= 1; sx++)
        {
            SampleMask(g, index, sx, sy, mask, w, h, bits);
            foreach (var (ch, t) in Templates)
            {
                var d = 0;
                for (var k = 0; k < bits.Length; k++) if (bits[k] != t.Fill[k]) d++;
                if (d < scores[ch]) scores[ch] = d;
            }
        }
        return scores;
    }

    private static void SampleMask(OsdLayout g, int index, int sx, int sy, bool[] mask, int w, int h, bool[] bits)
    {
        var cellX = g.Origin + index * g.Pitch + sx;
        var colW = g.Pitch / 8; // 7 colunas de glifo + 1 de espaço
        var rowH = g.Height / (double)Rows;
        for (var r = 0; r < Rows; r++)
        for (var c = 0; c < Cols; c++)
        {
            int xa = (int)Math.Round(cellX + c * colW), xb = (int)Math.Round(cellX + (c + 1) * colW);
            int ya = (int)Math.Round(g.Top + sy + r * rowH), yb = (int)Math.Round(g.Top + sy + (r + 1) * rowH);
            int on = 0, total = 0;
            for (var y = ya; y < yb; y++)
            for (var x = xa; x < xb; x++)
            {
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                total++;
                if (mask[y * w + x]) on++;
            }
            bits[r * Cols + c] = total > 0 && on * 2 >= total;
        }
    }

    /// <summary>
    /// Distância por contraste: conta "pixels da fonte" do traço que não estão claros e do contorno que não
    /// estão escuros. O fundo (demais pixels) é ignorado, então céu branco ou parede não atrapalham.
    /// Retorna null se a célula sair da imagem.
    /// </summary>
    private static Dictionary<char, int>? ContrastScores(OsdLayout g, int index, byte[] px, int w, int h)
    {
        if (index < 0) return null;
        var colW = g.Pitch / 8;
        var rowH = g.Height / (double)Rows;
        var scores = Templates.ToDictionary(t => t.Key, _ => int.MaxValue);
        var level = new int[(Cols + 2) * (Rows + 2)]; // grade com 1 pixel de fonte de borda (contorno externo)

        for (var sy = -1; sy <= 1; sy++)
        for (var sx = -1; sx <= 1; sx++)
        {
            var cellX = g.Origin + index * g.Pitch + sx;
            for (var r = -1; r <= Rows; r++)
            for (var c = -1; c <= Cols; c++)
            {
                // média do miolo do "pixel da fonte" (evita a borda borrada pela compressão)
                double cx = cellX + (c + 0.5) * colW, cy = g.Top + sy + (r + 0.5) * rowH;
                int xa = (int)Math.Round(cx - colW / 4), xb = (int)Math.Round(cx + colW / 4);
                int ya = (int)Math.Round(cy - rowH / 4), yb = (int)Math.Round(cy + rowH / 4);
                if (xa < 0 || ya < 0 || xb >= w || yb >= h) return null;
                int sum = 0, n = 0;
                for (var y = ya; y <= yb; y++)
                for (var x = xa; x <= xb; x++) { sum += px[y * w + x]; n++; }
                level[(r + 1) * (Cols + 2) + c + 1] = sum / n;
            }

            foreach (var (ch, t) in Templates)
            {
                var d = 0;
                for (var k = 0; k < level.Length; k++)
                {
                    if (t.FillPadded[k] && level[k] < 150) d++;
                    else if (t.Outline[k] && level[k] > 110) d++;
                }
                if (d < scores[ch]) scores[ch] = d;
            }
        }
        return scores;
    }

    private sealed class Template
    {
        public bool[] Fill { get; }        // 7x10
        public bool[] FillPadded { get; }  // 9x12 (com borda)
        public bool[] Outline { get; }     // 9x12: vizinhos (4-conexos) do traço que não são traço

        public Template(string rows)
        {
            Fill = rows.Replace("|", "").Select(c => c == '#').ToArray();
            const int pw = Cols + 2, ph = Rows + 2;
            FillPadded = new bool[pw * ph];
            Outline = new bool[pw * ph];
            for (var r = 0; r < Rows; r++)
            for (var c = 0; c < Cols; c++)
                FillPadded[(r + 1) * pw + c + 1] = Fill[r * Cols + c];
            for (var r = 0; r < ph; r++)
            for (var c = 0; c < pw; c++)
            {
                if (FillPadded[r * pw + c]) continue;
                bool F(int rr, int cc) => rr >= 0 && cc >= 0 && rr < ph && cc < pw && FillPadded[rr * pw + cc];
                Outline[r * pw + c] = F(r - 1, c) || F(r + 1, c) || F(r, c - 1) || F(r, c + 1);
            }
        }
    }

    // Modelos 7x10 extraídos de gravações reais de NVR Hikvision (720p); '#' = traço branco
    private static readonly Dictionary<char, Template> Templates = new Dictionary<char, string>
    {
        ['-'] = ".......|.......|.......|.......|.......|#######|.......|.......|.......|.......",
        ['0'] = "..###..|.##.##.|##...##|##...##|##.#.##|##.#.##|##...##|##...##|.##.##.|..###..",
        ['1'] = "...##..|..###..|.###...|...##..|...##..|...##..|...##..|...##..|...##..|.##..##",
        ['2'] = ".####..|##...##|.....##|....##.|...##..|..##...|.##....|##.....|##...##|#.#####",
        ['3'] = ".####..|##...##|.....##|.....##|..###..|.....##|.....##|.....##|##...##|.####..",
        ['4'] = "....##.|...###.|..###..|.##.##.|##..##.|####..#|....##.|....##.|....##.|...####",
        ['5'] = "..#####|##.....|##.....|##.....|..###..|.....##|.....##|.....##|##...##|.####..",
        ['6'] = "..###..|.##....|##.....|##.....|..###..|#....##|##...##|##...##|##...##|..###..",
        ['7'] = "#####..|##...##|.....##|.....##|....##.|...##..|..##...|..##...|..##...|..##...",
        ['8'] = "..###..|#....##|#....##|#....##|..###..|#....##|#....##|#....##|##...##|..###..",
        ['9'] = "..###..|##...##|##...##|##...##|..###..|.....##|.....##|.....##|....##.|.####..",
        [':'] = ".......|.......|...##..|...##..|.......|.......|.......|...##..|...##..|.......",
    }.ToDictionary(kv => kv.Key, kv => new Template(kv.Value));
}
