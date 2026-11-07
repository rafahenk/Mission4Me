<p align="center">
  <img src="src/Mission4Me/Assets/Mission4Me.png" width="112" alt="Mission4Me">
</p>

<h1 align="center">Mission4Me</h1>

<p align="center">
  Foco por missões no Windows: a barra de tarefas muda de cor enquanto você trabalha<br>
  e um contador mostra o projeto e o tempo que falta.
</p>

---

## O que é

Você define **no que vai trabalhar** e **por quanto tempo**. Enquanto a missão está ativa:

- a **barra de tarefas** fica verde-azulada (e volta à cor original quando você pausa ou encerra);
- um **contador** sobre a barra mostra o projeto, o tempo restante e o progresso — com um pulso de cor suave;
- interrupções, pausas e trocas de missão ficam registradas para você entender **para onde o seu tempo vai**.

## Recursos

| | |
|---|---|
| **Missão** | projeto + meta de tempo; editar, pausar, encerrar; avisos de reta final e de meta atingida |
| **Contador na barra** | estados: em missão · reta final · tempo extra · pausado; some sozinho em tela cheia |
| **Interrupções** | motivos prontos (ligação, celular, outro projeto…) ou texto livre; opção de devolver o tempo à meta |
| **A FAZER** | fila de próximas missões: adicionar, reordenar, editar, puxar para execução ou trocar com a atual |
| **Histórico** | filtros por período, status e projeto; resumo de foco e cumprimento; editar/excluir itens |
| **Atalho global** | `Ctrl+Alt+M` (configurável) abre a missão de qualquer lugar — mesmo com o app fechado |
| **Relatórios** | exporte o histórico (CSV para Excel) e a linha do tempo de eventos (JSONL) quando quiser |

## Instalação

1. Baixe o `Mission4Me-Setup-x.y.z.exe` em **[Releases](../../releases/latest)**.
2. Execute. A instalação é **por usuário** e **não pede administrador**.
3. Não é preciso instalar .NET: o executável já inclui o que precisa.

> **Aviso do Windows SmartScreen:** o instalador ainda não é assinado digitalmente, então o Windows pode
> mostrar "O Windows protegeu o computador". Clique em **Mais informações → Executar assim mesmo**.
> Para conferir que o arquivo é o original, compare o SHA-256 com o `.sha256` publicado no Release:
> ```powershell
> Get-FileHash .\Mission4Me-Setup-1.0.0.exe -Algorithm SHA256
> ```

**Requisitos:** Windows 10 (1809+) ou 11, 64 bits.

## Uso rápido

- **`Ctrl+Alt+M`** — nova missão (ou editar a atual).
- **Clique no contador** — editar, pausar, encerrar, registrar interrupção.
- **"Próxima missão"** (topo da janela) — guardar algo para depois sem largar o que está fazendo.
- **Abas laterais** — **A FAZER** (fila) e **HISTÓRICO**.
- **Ícone na bandeja** (botão direito) — posição do contador, pulso de cor, iniciar com o Windows, **exportar histórico**.

## Dados e privacidade

Tudo fica **no seu computador**; o Mission4Me não se conecta à internet.

Os dados ficam em `%APPDATA%\Mission4Me` e são **cifrados com a DPAPI do Windows**, vinculada à sua conta:
outro usuário do PC, uma cópia do disco ou um servidor de perfil móvel não conseguem lê-los.

| Arquivo | Conteúdo |
|---|---|
| `sessoes.csv` | uma linha por missão (início, fim, projeto, meta, trabalhado, pausas, interrupções, foco real, status) |
| `eventos.jsonl` | linha do tempo: início, pausa, interrupção, edição, fila… |
| `fila.json` · `config.json` · `missao-atual.json` | fila, preferências e missão em andamento |

Para usar em planilhas ou relatórios: **bandeja → Exportar histórico (cópia legível)…** gera um CSV
(abre no Excel) e um JSONL na pasta que você escolher. Detalhes em [SECURITY.md](SECURITY.md).

**O que ele altera no Windows (só no seu usuário, `HKCU`):** a cor de ênfase e o "mostrar ênfase na barra de tarefas"
durante a missão (com backup e restauração automática, inclusive após travamento), a entrada de inicialização
(se você ativar) e o atalho do Menu Iniciar com a tecla de atalho.

**Desinstalar** (Configurações → Aplicativos): restaura a barra, remove a inicialização automática e pergunta se deve
apagar o histórico.

## Compilar a partir do código

Requisitos: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) e, para o instalador,
[Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
winget install Microsoft.DotNet.SDK.8
winget install JRSoftware.InnoSetup

.\build.ps1                 # gera dist\Mission4Me-Setup-1.0.0.exe (+ .sha256)
.\build.ps1 -Version 1.1.0  # outra versão
.\build.ps1 -Dev            # build rápido para testar e já abre o app
```

(`build.bat` faz o mesmo com duplo clique.)

## Publicar uma versão

1. Atualize o `CHANGELOG.md`.
2. No GitHub: **Releases → Draft a new release**, crie a tag `vX.Y.Z` e clique em **Publish release**
   (ou pelo terminal: `gh release create v1.1.0 --generate-notes`).
3. O workflow `.github/workflows/release.yml` compila e anexa o instalador e o `.sha256` ao Release.

O instalador usa um `AppId` fixo, então novas versões **atualizam** a instalação existente
(o app é fechado de forma limpa antes da cópia).

## Estrutura

```
src/Mission4Me/        aplicativo (C# / WPF, .NET 8)
  Core/                regras: missão, fila, log, barra de tarefas, atalho
  UI/                  janelas: contador, missão/fila/histórico, bandeja
  Assets/              ícone
installer/             script do Inno Setup
build.ps1 / build.bat  compilação + instalador
.github/workflows/     release automático por tag
```

## Licença

[MIT](LICENSE) © 2026 HNKL Tecnologia

---

<p align="center"><sub>Feito por <b>HNKL Tecnologia</b></sub></p>
