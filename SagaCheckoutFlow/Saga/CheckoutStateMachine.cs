using System;
using System.Linq;
using Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;
using Saga.Events;

namespace Saga;

internal class CheckoutStateMachine : MassTransitStateMachine<CheckoutState>
{
    public State Created { get; private set; }
    public State Paid { get; private set; }
    public State PaymentFailed { get; private set; }
    public State MoneyRefundStarted { get; private set; }
    public State ProductReserved { get; private set; }
    public State ProductReservationFailed { get; private set; }
    public State DeliveryBooked { get; private set; }
    public State BookDeliveryFailed { get; private set; }
    public State Cancelled { get; private set; }
    public State Closed { get; private set; }
    public Event<Fault<OrderCreated>> FaultOrderCreated { get; private set; }
    public Event<OrderCreated> OrderCreated { get; private set; }
    public Event<PaymentSucceeded> PaymentSucceeded { get; private set; }
    public Event<PaymentFailed> PaymentFailedEvent { get; private set; }
    public Event<OrderStatusRequest> OrderStatusRequest { get; private set; }
    public Schedule<CheckoutState, OrderPaymentTimeoutExpired> OrderPaymentTimeout { get; private set; }
    public Event<ProductReserved> ProductReservedEvent { get; private set; }
    public Event<Fault<ReserveProductCommand>> FaultReserveProductCommand { get; private set; }
    public Request<CheckoutState, BookDeliveryRequest, BookDeliveryResponse> BookDelivery { get; private set; }
    public Event<DeliverySucceeded> DeliverySucceeded { get; private set; }
    public Event<MoneyRefunded> MoneyRefunded { get; private set; }

    public CheckoutStateMachine(ILogger<CheckoutStateMachine> logger, CheckoutSagaOptions options)
    {
        SetupEvents();

        Schedule(() => OrderPaymentTimeout, instance => instance.OrderPaymentTimeoutTokenId, s =>
        {
            s.Delay = TimeSpan.FromSeconds(options.PaymentTimeoutSeconds);

            s.Received = r => r.CorrelateBy<int>(state => state.OrderId, m => m.Message.OrderId);
        });
        InstanceState(x => x.CurrentState);

        Initially(
            When(OrderCreated)
                .Then(x =>
                {
                    x.Saga.OrderId = x.Message.OrderId;
                })
                .Schedule(OrderPaymentTimeout,
                    context => context.Init<OrderPaymentTimeoutExpired>(new OrderPaymentTimeoutExpired { OrderId = context.Saga.OrderId }))
                .TransitionTo(Created));

        During(Created, PaymentFailed,
            When(PaymentSucceeded)
                .Then(x => x.Saga.PaymentDate = x.Message.PaymentDate)
                .PublishAsync(x => x.Init<ReserveProductCommand>(new ReserveProductCommand
                {
                    OrderId = x.Saga.OrderId
                }))
                .Unschedule(OrderPaymentTimeout)
                .TransitionTo(Paid),
            When(PaymentFailedEvent)
                .Then(x => x.Saga.PaymentRetries += 1)
                .IfElse(x => x.Saga.PaymentRetries >= 3,
                    x => x.TransitionTo(Cancelled),
                    x => x.TransitionTo(PaymentFailed)));

        During(Paid,
            When(ProductReservedEvent)
                .TransitionTo(ProductReserved)
                .Request(BookDelivery, x => x.Init<BookDeliveryRequest>(new BookDeliveryRequest()))
                .TransitionTo(BookDelivery.Pending),
            When(FaultReserveProductCommand)
                .TransitionTo(ProductReservationFailed)
                .PublishAsync(x => x.Init<RefundMoneyCommand>(
                    new RefundMoneyCommand
                    {
                        OrderId = x.Saga.OrderId
                    }))
                .TransitionTo(MoneyRefundStarted)
        );

        During(BookDelivery.Pending,
            When(BookDelivery.Completed)
                .Then(x => x.Saga.DeliveryId = x.Message.DeliveryId)
                .TransitionTo(DeliveryBooked),
            When(BookDelivery.Faulted)
                .TransitionTo(BookDeliveryFailed)
                .PublishAsync(x => x.Init<RefundMoneyCommand>(new RefundMoneyCommand
                {
                    OrderId = x.Saga.OrderId
                }))
                .TransitionTo(MoneyRefundStarted),
            When(BookDelivery.TimeoutExpired)
                .TransitionTo(BookDeliveryFailed)
                .PublishAsync(x => x.Init<RefundMoneyCommand>(new RefundMoneyCommand
                {
                    OrderId = x.Saga.OrderId
                }))
                .TransitionTo(MoneyRefundStarted));

        During(DeliveryBooked,
            When(DeliverySucceeded)
                .Then(x => x.Saga.DeliveryDate = DateTime.Now)
                .TransitionTo(Closed));

        During(MoneyRefundStarted,
            When(MoneyRefunded)
                .TransitionTo(Cancelled));

        DuringAny(
            When(OrderPaymentTimeout?.Received)
                .Unschedule(OrderPaymentTimeout)
                .TransitionTo(Cancelled),
            When(FaultOrderCreated).Then(x =>
                logger.LogInformation("Something went wrong with Handling OrderCreated: {Exception}",
                    x.Message.Exceptions.FirstOrDefault()?.Message)),
            When(OrderStatusRequest)
                .Then(x => x.Saga.RequestCount += 1)
                .Then(x =>
                {
                    // Demo purpose - generate fail scenario to trigger retry
                    // if (x.Saga.RequestCount % 3 == 0)
                    // {
                    //     throw new Exception();
                    // }
                })
                .RespondAsync(x => x.Init<OrderStatusResponse>(new OrderStatusResponse
                {
                    CurrentState = x.Saga.CurrentState,
                    OrderId = x.Saga.OrderId,
                    DeliveryId = x.Saga.DeliveryId,
                    PaymentDate = x.Saga.PaymentDate,
                    PaymentRetries = x.Saga.PaymentRetries,
                })));
    }

    private void SetupEvents()
    {
        Event(() => OrderCreated,
            e => e.CorrelateBy<int>(state => state.OrderId, m => m.Message.OrderId)
                .SelectId(x => x.CorrelationId ?? NewId.NextGuid()));

        Event(() => FaultOrderCreated, e => e.CorrelateBy<int>(state => state.OrderId, m => m.Message.Message.OrderId));
        Event(() => PaymentSucceeded, e => e.CorrelateBy<int>(state => state.OrderId, m => m.Message.OrderId));
        Event(() => PaymentFailedEvent, e => e.CorrelateBy<int>(state => state.OrderId, m => m.Message.OrderId));
        Event(() => ProductReservedEvent, e => e.CorrelateBy<int>(state => state.OrderId, m => m.Message.OrderId));
        Event(() => FaultReserveProductCommand,
            e => e.CorrelateBy<int>(state => state.OrderId, m => m.Message.Message.OrderId));
        Event(() => DeliverySucceeded, e => e.CorrelateBy<int>(state => state.OrderId, m => m.Message.OrderId));
        Event(() => MoneyRefunded, e => e.CorrelateBy<int>(state => state.OrderId, m => m.Message.OrderId));

        Request(() => BookDelivery);

        Event(() => OrderStatusRequest, x =>
        {
            x.CorrelateBy<int>(state => state.OrderId,
                m => m.Message.OrderId);

            x.OnMissingInstance(m => m.ExecuteAsync(async context =>
            {
                if (context.RequestId.HasValue)
                {
                    await context.RespondAsync(
                        new OrderNotFoundResponse(context.Message.OrderId));
                }
            }));
        });
    }
}