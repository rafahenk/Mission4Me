# Segurança

## Como reportar uma vulnerabilidade

Use o **reporte privado do GitHub**: aba **Security → Report a vulnerability** deste repositório.
Não abra *issue* pública para falhas de segurança.

## Modelo de segurança do Mission4Me

- **Sem rede:** o app não faz nenhuma conexão com a internet.
- **Sem administrador:** instala por usuário e só altera configurações do próprio usuário (`HKCU`).
- **Dados cifrados em repouso:** histórico, eventos, fila, configurações e missão em andamento ficam em
  `%APPDATA%\Mission4Me`, cifrados com a **DPAPI** do Windows vinculada à sua conta. Outro usuário do PC,
  uma cópia do disco/backup ou um servidor de perfil móvel não conseguem ler o conteúdo.
  *Limite:* um programa malicioso rodando na sua própria conta conseguiria ler — isso vale para qualquer
  aplicativo de usuário.
- **Exportação explícita:** cópias legíveis (CSV/JSONL) só são geradas quando você usa "Exportar histórico",
  com proteção contra *CSV injection* (texto que começaria com `=`, `+`, `-`, `@` não vira fórmula no Excel).
- **Logs sem dados pessoais:** o `erros.log` troca a pasta do usuário por `%USERPROFILE%`; o executável é
  compilado sem caminhos da máquina de build.
- **Integridade do instalador:** cada Release publica o SHA-256 do instalador.
