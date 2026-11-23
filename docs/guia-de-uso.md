# Guia de uso do SpentZ

## Como usar

1. Abra `SpentZ.exe` no Windows 10 ou 11. No primeiro acesso, informe quanto você tem disponível hoje, separado do Cofrinho. O saldo inicial acompanha os meses seguintes e não entra como ganho. Se quiser definir depois ou já tiver usado o aplicativo, abra **Configurações → Saldo inicial**.
2. No **Chat**, digite `42,90 almoço` ou `almoço 42,90` para uma despesa e `recebi 150 freela` para um ganho. A confirmação oferece **Editar** e **Desfazer**. O chat mostra os registros mais recentes de até três dias, e os demais ficam em **Lançamentos**.
3. No **Resumo mensal**, confira o disponível e a previsão até o fim do mês selecionado. Abra **Como a previsão foi calculada** para entender as pendências de cada período. Em **Detalhes do mês**, veja os totais e, no primeiro mês, as despesas que aconteceram desde o saldo inicial. **Ajustar saldo** registra uma diferença real na conta. Se o valor informado no início foi digitado errado, use **Configurações → Corrigir saldo inicial**. **Fechamento** abre a prévia e os relatórios em CSV.
4. Em **Ganhos**, clique em **Adicionar ganho** para lançar uma entrada pontual. Para repetir um salário todo mês, abra **Ganhos fixos → Novo ganho fixo** e informe a primeira data prevista. Use **Marcar como recebido** quando o valor entrar. O saldo previsto considera as entradas programadas e o saldo registrado considera os recebimentos confirmados.
5. Em **Contas futuras**, use **Este mês**, **Próximo mês** e **Vencidas** para ver as pendências. Se o primeiro vencimento já chegou, escolha se ele está em aberto, se o valor estava no saldo inicial ou se foi movimentado depois. Quando houver um lançamento parecido, use **Vincular a um lançamento existente** para reaproveitá-lo. Em **Planos ativos**, use **Alterar próximos vencimentos** para mudanças futuras. **Cancelar renovação** retira as próximas cobranças, e uma cobrança perdoada pode ser dispensada.
6. Em **Lançamentos**, busque por descrição ou filtre por tipo e categoria. Use **Registrar estorno** quando o dinheiro de uma compra voltar. O estorno aparece na data da devolução e a compra original continua no histórico. **Exportar mês** inclui os lançamentos do mês em CSV.
7. Em **Configurações**, escolha o modo claro ou noturno, a cor de destaque e a antecedência dos avisos de vencimento. Os avisos aparecem dentro do SpentZ enquanto ele está aberto.
8. No chat ou no resumo, clique em **Cofrinho**. Veja o total guardado, escolha **Depositar** ou **Sacar** e informe o local, o valor e a data. Na aba **Cofrinho**, você pode editar uma movimentação anterior. O resumo e o fechamento mostram os valores, e os saques aumentam o disponível do mês.
9. No **Resumo mensal**, use **Criar categoria** para adicionar um nome e **Definir limite** para escolher uma categoria pronta ou personalizada e informar quanto quer gastar por mês. Por exemplo, crie **Carta Pokémon** e digite `20 carta pokemon` no Chat. O app reconhece nomes sem acento e pequenos erros de digitação quando há uma correspondência clara. Confira a categoria na confirmação do Chat. O gasto conta no limite da categoria. Um estorno da mesma compra recebido no mesmo mês devolve espaço ao limite. Ajustes de saldo ficam fora dos limites.

O SpentZ acompanha a data e o fuso configurados no Windows. Com o aplicativo aberto, os avisos e a visão do mês atual se atualizam na virada do dia. Clique no nome do mês ou use **Ctrl + G** para escolher mês e ano. **Voltar ao mês atual** retorna com um clique. No chat, mensagens com apenas o valor são despesas em **Sem categoria**. Use vírgula para os centavos.

## Dados e backup

Antes de formatar ou trocar de computador, abra **Configurações → Backup e restauração**. Clique em **Salvar backup** e escolha onde criar a pasta que reúne o `SpentZ.exe` e os dados. Guarde a pasta inteira em um pendrive, HD externo ou unidade de nuvem e confira se consegue abri-la no destino.

No novo Windows, abra o `SpentZ.exe` que está dentro dessa pasta e selecione **Configurações → Backup e restauração → Restaurar backup**. Aceite a pasta sugerida ou selecione a pasta manualmente, mesmo que ela tenha sido renomeada. Confira a quantidade e o intervalo de datas dos lançamentos antes de confirmar. A restauração troca os registros atuais pelos do backup e salva uma cópia dos dados anteriores na pasta local `Backups`.

O banco em uso continua em `%LOCALAPPDATA%\GastoEmDia\gastos.sqlite3` para preservar os lançamentos das versões anteriores. O CSV de Lançamentos lista entradas e despesas. O CSV de Fechamento também inclui limites, ajustes, Cofrinho e o vencimento e a data de pagamento de contas programadas. O backup abrange todos os dados e preferências.

Para começar do zero, use **Resetar** no fim das **Configurações**. A confirmação apaga todos os gastos, ganhos, contas futuras, reservas e fechamentos do banco em uso. As preferências do app e os arquivos de backup já salvos são preservados. Salve um backup antes de confirmar se quiser recuperar esses registros depois.

## Referências de produto

O fluxo de previsão e confirmação se baseia nos [lançamentos programados do YNAB](https://support.ynab.com/scheduled-transactions-a-guide-BygrAIFA9) e nas [cobranças programadas do Actual Budget](https://actualbudget.org/docs/schedules/). No SpentZ, o saldo mensal separa o que foi registrado do que ainda está previsto.
