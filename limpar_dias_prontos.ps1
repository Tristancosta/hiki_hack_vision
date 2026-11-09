# limpar_dias_prontos.ps1
# Apaga os videos da pasta informada que NAO estejam dentro do intervalo de dias informado.
# Depois renumera os videos que sobraram: o primeiro dia fica com _1, o seguinte com _2, etc.
# Os nomes sao ddMMhhmm_x.mp4 (gerados pela normalizacao). Arquivos fora desse padrao sao ignorados.
# O intervalo e por dia/mes, sem ano: 20/09 a 30/09 mantem os dias de 20 a 30 de setembro.
# Se o inicio for depois do fim (ex.: 28/12 a 05/01), o intervalo atravessa o ano.
#
# Uso: execute limpar_dias_prontos.bat (duplo clique). Ou pelo terminal:
#   powershell -ExecutionPolicy Bypass -File limpar_dias_prontos.ps1 -Pasta "D:\videos\prontos" -Inicio 26/09 -Fim 27/09

param(
    [string]$Pasta,
    [string]$Inicio,
    [string]$Fim
)

$ErrorActionPreference = 'Stop'

if (-not $Pasta) {
    $Pasta = Read-Host "Caminho absoluto da pasta prontos (ex.: D:\videos\prontos)"
}
$Pasta = $Pasta.Trim().Trim('"')
if (-not [System.IO.Path]::IsPathRooted($Pasta)) { throw "Informe um caminho absoluto (ex.: D:\videos\prontos)." }
if (-not (Test-Path -LiteralPath $Pasta -PathType Container)) { throw "Pasta nao encontrada: $Pasta" }
$pasta = (Resolve-Path -LiteralPath $Pasta).ProviderPath

# Converte dd/MM em numero do dia do ano (usa ano 2000, bissexto, para aceitar 29/02)
function Get-Ordinal([string]$texto) {
    $m = [regex]::Match($texto.Trim(), '^(\d{1,2})/(\d{1,2})$')
    if (-not $m.Success) { throw "Data invalida: '$texto'. Use o formato dd/MM." }
    try {
        return ([datetime]::new(2000, [int]$m.Groups[2].Value, [int]$m.Groups[1].Value)).DayOfYear
    } catch {
        throw "Data invalida: '$texto'."
    }
}

if (-not $Inicio) { $Inicio = Read-Host "Dia de INICIO do intervalo a manter (dd/MM)" }
if (-not $Fim)    { $Fim    = Read-Host "Dia de FIM do intervalo a manter (dd/MM)" }

$ordIni = Get-Ordinal $Inicio
$ordFim = Get-Ordinal $Fim

function Test-DentroDoIntervalo([int]$ord) {
    if ($ordIni -le $ordFim) { return ($ord -ge $ordIni -and $ord -le $ordFim) }
    # intervalo atravessa o ano (ex.: 28/12 a 05/01)
    return ($ord -ge $ordIni -or $ord -le $ordFim)
}

# Posicao do dia dentro do intervalo, contada a partir do inicio (0 = primeiro dia do intervalo)
function Get-PosicaoNoIntervalo([int]$ord) {
    return (($ord - $ordIni + 366) % 366)
}

$paraApagar = New-Object System.Collections.Generic.List[object]
$paraManter = New-Object System.Collections.Generic.List[object]
$ignorados  = New-Object System.Collections.Generic.List[object]

foreach ($f in Get-ChildItem -LiteralPath $pasta -Filter *.mp4 -File) {
    $m = [regex]::Match($f.Name, '^(\d{2})(\d{2})\d{4}_\d+\.mp4$')
    if (-not $m.Success) { $ignorados.Add($f); continue }
    $ord = $null
    try {
        $ord = ([datetime]::new(2000, [int]$m.Groups[2].Value, [int]$m.Groups[1].Value)).DayOfYear
    } catch { }
    if ($null -eq $ord) { $ignorados.Add($f); continue }
    if (Test-DentroDoIntervalo $ord) { $paraManter.Add($f) } else { $paraApagar.Add($f) }
}

# Numero novo de cada dia que sobra: 1 para o primeiro dia, 2 para o seguinte, etc.
$posicoes = @{}
foreach ($f in $paraManter) {
    $dm = $f.Name.Substring(0, 4)
    $ord = ([datetime]::new(2000, [int]$dm.Substring(2, 2), [int]$dm.Substring(0, 2))).DayOfYear
    $posicoes[$dm] = Get-PosicaoNoIntervalo $ord
}
$numeroDoDia = @{}
$n = 1
foreach ($dm in ($posicoes.GetEnumerator() | Sort-Object Value | ForEach-Object { $_.Key })) {
    $numeroDoDia[$dm] = $n
    $n++
}

# Calcula os renomes e confere se nao ha dois arquivos com o mesmo nome final
$renomes = New-Object System.Collections.Generic.List[object]
foreach ($f in $paraManter) {
    $dm = $f.Name.Substring(0, 4)
    $novoNome = $f.Name.Substring(0, 8) + "_" + $numeroDoDia[$dm] + ".mp4"
    if ($novoNome -ne $f.Name) { $renomes.Add([pscustomobject]@{ Arquivo = $f; Novo = $novoNome }) }
}
$duplicados = $paraManter | ForEach-Object { $_.Name.Substring(0, 8) + "_" + $numeroDoDia[$_.Name.Substring(0, 4)] + ".mp4" } |
    Group-Object | Where-Object { $_.Count -gt 1 }
if ($duplicados) {
    $lista = ($duplicados | ForEach-Object { $_.Name }) -join ', '
    throw "Nao foi possivel renumerar: nomes iguais seriam gerados ($lista). Nada foi alterado."
}

Write-Host ""
Write-Host "Intervalo mantido : $Inicio a $Fim"
Write-Host "Videos mantidos   : $($paraManter.Count)"
Write-Host "Videos a apagar   : $($paraApagar.Count)"
Write-Host "Videos a renumerar: $($renomes.Count)"
Write-Host "Ignorados (nome fora do padrao): $($ignorados.Count)"

if ($paraApagar.Count -eq 0 -and $renomes.Count -eq 0) {
    Write-Host ""
    Write-Host "Nada para apagar nem renumerar."
    return
}

if ($paraApagar.Count -gt 0) {
    Write-Host ""
    Write-Host "Resumo do que sera apagado, por dia:"
    $paraApagar | ForEach-Object { $_.Name.Substring(0, 4) } |
        Group-Object | Sort-Object Name |
        ForEach-Object {
            $dd = $_.Name.Substring(0, 2)
            $mm = $_.Name.Substring(2, 2)
            Write-Host ("  {0}/{1}: {2} arquivo(s)" -f $dd, $mm, $_.Count)
        }
}

if ($numeroDoDia.Count -gt 0) {
    Write-Host ""
    Write-Host "Numeracao dos dias que ficam:"
    foreach ($dm in ($numeroDoDia.GetEnumerator() | Sort-Object Value | ForEach-Object { $_.Key })) {
        Write-Host ("  {0}/{1}: _{2}" -f $dm.Substring(0, 2), $dm.Substring(2, 2), $numeroDoDia[$dm])
    }
}

Write-Host ""
$confirmacao = Read-Host "Digite APAGAR (em maiusculas) para confirmar. Qualquer outra coisa cancela"
if ($confirmacao -cne 'APAGAR') {
    Write-Host "Cancelado. Nenhum arquivo foi alterado."
    return
}

$ok = 0
$falhas = 0
foreach ($f in $paraApagar) {
    try {
        Remove-Item -LiteralPath $f.FullName -Force
        $ok++
    } catch {
        $falhas++
        Write-Host "Falha ao apagar $($f.Name): $($_.Exception.Message)"
    }
}

# Renomeia em duas etapas (nome temporario primeiro) para nao colidir com nomes ja existentes
$temporarios = New-Object System.Collections.Generic.List[object]
$idx = 0
foreach ($r in $renomes) {
    $idx++
    $tmp = "__tmp_renum_$idx.mp4"
    try {
        Rename-Item -LiteralPath $r.Arquivo.FullName -NewName $tmp
        $temporarios.Add([pscustomobject]@{ Tmp = $tmp; Novo = $r.Novo; Antigo = $r.Arquivo.Name })
    } catch {
        $falhas++
        Write-Host "Falha ao renomear $($r.Arquivo.Name): $($_.Exception.Message)"
    }
}
$renomeados = 0
foreach ($t in $temporarios) {
    try {
        Rename-Item -LiteralPath (Join-Path $pasta $t.Tmp) -NewName $t.Novo
        $renomeados++
    } catch {
        $falhas++
        Write-Host "Falha ao renomear $($t.Antigo) para $($t.Novo): $($_.Exception.Message)"
    }
}

Write-Host ""
Write-Host "Apagados: $ok. Renumerados: $renomeados. Falhas: $falhas."
