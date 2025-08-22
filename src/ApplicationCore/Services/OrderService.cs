using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Ardalis.GuardClauses;
using Azure.Messaging.ServiceBus;
using MediatR;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate.Events;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;
using Microsoft.Extensions.Configuration;

namespace Microsoft.eShopWeb.ApplicationCore.Services;

public class OrderService : IOrderService
{
    private readonly IRepository<Order> _orderRepository;
    private readonly IUriComposer _uriComposer;
    private readonly IRepository<Basket> _basketRepository;
    private readonly IRepository<CatalogItem> _itemRepository;
    private readonly IMediator _mediator;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ServiceBusClient _sbClient;

    public OrderService(IRepository<Basket> basketRepository,
        IRepository<CatalogItem> itemRepository,
        IRepository<Order> orderRepository,
        IUriComposer uriComposer, IMediator mediator,
        IHttpClientFactory httpClientFactory, IConfiguration configuration, ServiceBusClient sbClient)
    {
        _orderRepository = orderRepository;
        _uriComposer = uriComposer;
        _basketRepository = basketRepository;
        _itemRepository = itemRepository;
        _mediator = mediator;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _sbClient = sbClient;
    }

    public async Task CreateOrderAsync(int basketId, Address shippingAddress)
    {
        var basketSpec = new BasketWithItemsSpecification(basketId);
        var basket = await _basketRepository.FirstOrDefaultAsync(basketSpec);

        Guard.Against.Null(basket, nameof(basket));
        Guard.Against.EmptyBasketOnCheckout(basket.Items);

        var catalogItemsSpecification = new CatalogItemsSpecification(basket.Items.Select(item => item.CatalogItemId).ToArray());
        var catalogItems = await _itemRepository.ListAsync(catalogItemsSpecification);

        var items = basket.Items.Select(basketItem =>
        {
            var catalogItem = catalogItems.First(c => c.Id == basketItem.CatalogItemId);
            var itemOrdered = new CatalogItemOrdered(catalogItem.Id, catalogItem.Name, _uriComposer.ComposePicUri(catalogItem.PictureUri));
            var orderItem = new OrderItem(itemOrdered, basketItem.UnitPrice, basketItem.Quantity);
            return orderItem;
        }).ToList();

        var order = new Order(basket.BuyerId, shippingAddress, items);

        await _orderRepository.AddAsync(order);
        OrderCreatedEvent orderCreatedEvent = new OrderCreatedEvent(order);
        
        await _mediator.Publish(orderCreatedEvent);

        /*var orderData = new
        {
            OrderId = order.Id,
            Items = order.OrderItems.Select(item => new
            {
                ItemId = item.Id,
                Quantity = item.Units
            })
        };**/

        var deliveryData = new
        {
            id = Guid.NewGuid().ToString(),
            OrderId = order.Id,
            Items = order.OrderItems.Select(item => new
            {
                ItemId = item.Id,
                Quantity = item.Units,
                Price = item.UnitPrice
            }),
            FinalPrice = order.Total(),
            ShippingAddress = new 
            {
                Street = order.ShipToAddress.Street,
                City = order.ShipToAddress.City,
                State = order.ShipToAddress.State,
                Country = order.ShipToAddress.Country,
                ZipCode = order.ShipToAddress.ZipCode
            }
        };

        //var jsonContent = new StringContent(JsonSerializer.Serialize(deliveryData), Encoding.UTF8, "application/json");

        var json = JsonSerializer.Serialize(deliveryData);

        var queue = _configuration["QueueName"] ?? "order-items-reserver";

        var sbSender = _sbClient.CreateSender(queue);

        var msg = new ServiceBusMessage(Encoding.UTF8.GetBytes(json))
        {
            ContentType = "application/json",
            CorrelationId = order.Id.ToString()
        };

        //await sbSender.SendMessageAsync(msg);

        //var client = _httpClientFactory.CreateClient("OrderItemsReserver");

        //var client = _httpClientFactory.CreateClient("OrderDelivery");

        //var response = await client.PostAsync("", jsonContent);

    }
}
