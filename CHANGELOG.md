# Changelog

Formato baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/); versões seguem [SemVer](https://semver.org/lang/pt-BR/).

## [1.0.0] — 2026-11-07

Primeira versão pública.

### Adicionado
- Missão de foco: projeto + tempo-alvo; a barra de tarefas fica verde-azulada durante a missão e volta à cor original ao pausar/encerrar.
- Contador sobre a barra (projeto, tempo restante, progresso), com estados: em missão, reta final, tempo extra, pausado.
- Pulso de cor suave (Oklab, 60 Hz, padrão 15×/min), configurável ou desligável.
- Janela de missão: iniciar, editar, pausar, encerrar, registrar interrupção (motivos padrão + texto livre, opção de acrescentar o tempo à meta).
- Fila "A FAZER": cadastrar próximas missões, reordenar, editar, trazer para execução ou trocar com a atual.
- Histórico com filtros (período, status, projeto), resumo, edição e exclusão (com backup automático).
- Tecla de atalho global configurável (padrão Ctrl+Alt+M), funciona também com o app fechado.
- Registro para relatórios: `sessoes.csv` (resumo por missão) e `eventos.jsonl` (linha do tempo).
- Instalador por usuário (sem administrador), atualização e desinstalação limpas.
