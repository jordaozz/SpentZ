# Guia de uso do SpentZ

[Voltar ao projeto](../README.md) · [Baixar para Windows](https://github.com/jordaozz/SpentZ/releases/latest/download/SpentZ.exe)

## Como usar

1. Abra `SpentZ.exe` no Windows 10 ou 11. No primeiro acesso, informe quanto você tem disponível hoje, separado do Cofrinho. O saldo inicial acompanha os meses seguintes e não entra como ganho. Se quiser definir depois ou já tiver usado o aplicativo, abra **Configurações → Saldo inicial**.
2. No **Chat**, digite `paguei 150 reais em uma peça da moto` para uma despesa ou `ganhei 150 de um free lance` para um ganho. A palavra-chave muda de cor e o tipo aparece abaixo do campo. `25 assinatura do Discord Nitro` abre o cadastro com valor, nome, frequência e primeira data para revisar. `parcelei 1200 em 6x` abre a revisão do parcelamento. Na confirmação de um lançamento simples, use **Editar** ou **Desfazer**.
3. No **Resumo mensal**, confira o disponível e a previsão até o fim do mês selecionado. Abra **Como a previsão foi calculada** para entender as pendências de cada período. Em **Detalhes do mês**, veja os totais e, no primeiro mês, as despesas que aconteceram desde o saldo inicial. **Ajustar saldo** registra uma diferença real na conta. Se o valor informado no início foi digitado errado, use **Configurações → Corrigir saldo inicial**. **Fechamento** abre a prévia e os relatórios em CSV.
4. Em **Ganhos**, clique em **Adicionar ganho** para lançar uma entrada pontual. Para repetir um salário todo mês, abra **Ganhos fixos → Novo ganho fixo** e informe a primeira data prevista. Use **Marcar como recebido** quando o valor entrar. O saldo previsto considera as entradas programadas e o saldo registrado considera os recebimentos confirmados.
5. Em **Contas futuras**, use **Este mês**, **Próximo mês** e **Vencidas** para ver as pendências. Se o primeiro vencimento já chegou, escolha se ele está em aberto, se o valor estava no saldo inicial ou se foi movimentado depois. Ao selecionar um plano ativo, o lápis abre a edição. Quando já existe pagamento confirmado, as alterações passam a valer nas próximas cobranças. A opção **Transformar em assinatura** reaproveita uma despesa de assinatura que já está em **Lançamentos**. **Cancelar renovação** retira as próximas cobranças, e uma cobrança perdoada pode ser dispensada.
6. Em **Lançamentos**, busque por descrição ou filtre por tipo e categoria. Use **Registrar estorno** quando o dinheiro de uma compra voltar. O estorno aparece na data da devolução e a compra original continua no histórico. **Exportar mês** inclui os lançamentos do mês em CSV.
7. Em **Configurações**, escolha o modo claro ou noturno, a cor de destaque e a antecedência dos avisos de vencimento. Os avisos aparecem dentro do SpentZ enquanto ele está aberto.
8. No chat ou no resumo, clique em **Cofrinho**. Veja o total guardado, escolha **Depositar** ou **Sacar** e informe o local, o valor e a data. Na aba **Cofrinho**, você pode editar uma movimentação anterior. O resumo e o fechamento mostram os valores, e os saques aumentam o disponível do mês.
9. No **Resumo mensal**, use **Criar categoria** para adicionar um nome e **Definir limite** para escolher uma categoria pronta ou personalizada e informar quanto quer gastar por mês. Por exemplo, crie **Carta Pokémon** e digite `20 carta pokemon` no Chat. O app reconhece nomes sem acento e pequenos erros de digitação quando há uma correspondência clara. Confira a categoria na confirmação do Chat. O gasto conta no limite da categoria. Um estorno da mesma compra recebido no mesmo mês devolve espaço ao limite. Ajustes de saldo ficam fora dos limites.

O SpentZ acompanha a data e o fuso configurados no Windows. Com o aplicativo aberto, os avisos e a visão do mês atual se atualizam na virada do dia. Clique no nome do mês ou use **Ctrl + G** para escolher mês e ano. **Voltar ao mês atual** retorna com um clique. No chat, mensagens com apenas o valor são despesas em **Sem categoria**. Use vírgula para os centavos.

## Acompanhar conta

No **Resumo mensal**, clique em **Acompanhar conta** para ver a posição de hoje. O saldo após contas desconta as pendências até o fim do mês, inclusive as vencidas de meses anteriores. O Cofrinho e os ganhos esperados aparecem à parte. Em **Simular uma compra**, informe um valor para ver quanto restaria ou faltaria. A simulação não cria um lançamento.

## Metas do Cofrinho

Abra **Cofrinho → Ver metas → Criar meta**. Informe o nome do objetivo e o valor desejado, como Moto e R$ 12.000. Use **Guardar dinheiro** para registrar o saldo do local. Depois, em **Metas**, use **Reservar valor**. Você pode separar valores para várias metas no mesmo banco.

Use **Já tinha guardado** para cadastrar dinheiro que já estava separado. **Guardar dinheiro** registra uma transferência feita agora. Reservar um valor para uma meta preserva o total guardado e o disponível. **Usar na compra** registra a saída da meta e a despesa juntas. **Detalhes e histórico** mostra as compras e oferece ações para desfazer, liberar ou arquivar. **Ver arquivadas** abre a lista de metas arquivadas. Nela, **Voltar** leva à lista de metas ativas.

## Dados e backup

Antes de formatar ou trocar de computador, abra **Configurações → Backup e restauração → Salvar backup protegido**. Guarde o arquivo .spentz e mantenha a senha em um local separado. A senha é necessária para recuperar a cópia.

No novo Windows, abra a versão atual do SpentZ e selecione **Configurações → Backup e restauração → Restaurar backup**. Escolha o arquivo .spentz ou o .sqlite3 de uma cópia antiga. O app verifica os registros antes da troca e guarda uma cópia protegida dos dados anteriores na pasta local `Backups`. O banco em uso e os arquivos CSV continuam legíveis no computador.

O banco em uso continua em `%LOCALAPPDATA%\GastoEmDia\gastos.sqlite3` para preservar os lançamentos das versões anteriores. O CSV de Lançamentos lista entradas e despesas. O CSV de Fechamento também inclui limites, ajustes, Cofrinho e o vencimento e a data de pagamento de contas programadas. O backup leva os registros e as preferências de aparência e avisos.

Para começar do zero, use **Resetar** no fim das **Configurações**. A confirmação apaga todos os gastos, ganhos, contas futuras, reservas e fechamentos do banco em uso. As preferências do app e os arquivos de backup já salvos são preservados. Salve um backup antes de confirmar se quiser recuperar esses registros depois.

## Referências de produto

O fluxo de previsão e confirmação se baseia nos [lançamentos programados do YNAB](https://support.ynab.com/scheduled-transactions-a-guide-BygrAIFA9) e nas [cobranças programadas do Actual Budget](https://actualbudget.org/docs/schedules/). No SpentZ, o saldo mensal separa o que foi registrado do que ainda está previsto.

O registro em linguagem natural tem como referência o [Porquim](https://www.porquim.com/). O acompanhamento por objetivo usa como referência as [metas do YNAB](https://www.ynab.com/features/goal-tracking) e as [Caixinhas do Nubank](https://nubank.com.br/ultravioleta/conta/caixinhas).
