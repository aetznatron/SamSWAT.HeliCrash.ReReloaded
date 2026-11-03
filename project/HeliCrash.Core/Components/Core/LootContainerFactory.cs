using System;
using System.Linq;
using System.Threading;
using Comfort.Common;
using Diz.Jobs;
using Cysharp.Threading.Tasks;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using JetBrains.Annotations;
using SamSWAT.HeliCrash.ArysReloaded.Utils;
using SPT.Reflection.Utils;
using ZLinq;

namespace SamSWAT.HeliCrash.ArysReloaded;

[UsedImplicitly]
public class LootContainerFactory(
    Logger logger,
    ConfigurationService configService,
    LocalizationService localizationService
)
{
    /// <summary>
    /// Creates the loot container and loads the bundles for the container and its contents.
    /// </summary>
    /// <param name="container">The container's <see cref="LootableContainer"/> component which allows player interaction.</param>
    /// <param name="containerItem">The container's <see cref="Item"/> which includes all its <see cref="Item"/> contents.</param>
    /// <param name="cancellationToken">Token to cancel the task.</param>
    /// <returns>The NetId of the container.</returns>
    /// <exception cref="NullReferenceException">When the <see cref="container"/> is null.</exception>
    public async UniTask<int> CreateContainer(
        LootableContainer container,
        Item containerItem,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (containerItem == null)
            {
                throw new NullReferenceException("Container item is null!");
            }

            if (configService.LoggingEnabled.Value)
            {
                logger.LogInfo("Creating loot container...");
            }

            container.Id = containerItem.Id;
            int netId = container.NetId;

            GameWorld gameWorld = Singleton<GameWorld>.Instance;

            if (gameWorld.World != null)
            {
                gameWorld.RegisterWorldInteractionObject(container);
            }

            LootItem.CreateLootContainer(
                container,
                containerItem,
                localizationService.Localize("containerName"),
                gameWorld
            );

            ResourceKey[] resourceKeys = GetResourceKeys(containerItem);

            await LoadItemBundles(resourceKeys, cancellationToken);

            if (configService.LoggingEnabled.Value)
            {
                logger.LogInfo(
                    $"Created loot container and loaded item bundles! NetId={netId.ToString()}"
                );
            }

            return netId;
        }
        catch (OperationCanceledException ex)
        {
            if (configService.LoggingEnabled.Value)
            {
                logger.LogInfo($"Canceled creating HeliCrash loot crate. {ex.Message}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(
                $"Failed to create HeliCrash loot crate! {ex.Message}\n{ex.StackTrace}"
            );
            return 0;
        }
    }

    private async UniTask LoadItemBundles(
        ResourceKey[] resourceKeys,
        CancellationToken cancellationToken
    )
    {
        ObjectsFactory objectsFactory = GetObjectsFactory();
        if (objectsFactory == null)
        {
            logger.LogWarning(
                "ObjectsFactory not reachable on GameWorld - skipping bundle preload for the loot container"
            );
            return;
        }

        await objectsFactory.LoadBundlesAndCreatePools(
            ObjectsFactory.PoolsCategory.Raid,
            ObjectsFactory.AssemblyType.Local,
            resourceKeys,
            JobYieldPriority.Immediate,
            null,
            cancellationToken
        );
    }

    private static AccessTools.FieldRef<GameWorld, ObjectsFactory> s_objectsFactoryRef;
    private static bool s_objectsFactoryLookupFailed;

    /// <summary>
    /// SPT 4.1 no longer exposes the object factory through <c>Singleton&lt;PoolManagerClass&gt;</c>
    /// and does not register it in the mod's VContainer scope either; the live instance hangs
    /// off <see cref="GameWorld"/> as a non-public field.
    /// </summary>
    private static ObjectsFactory GetObjectsFactory()
    {
        if (s_objectsFactoryLookupFailed)
        {
            return null;
        }

        try
        {
            s_objectsFactoryRef ??= AccessTools.FieldRefAccess<GameWorld, ObjectsFactory>("ObjectsFactory");
            GameWorld gameWorld = Singleton<GameWorld>.Instance;
            return gameWorld == null ? null : s_objectsFactoryRef(gameWorld);
        }
        catch (Exception)
        {
            s_objectsFactoryLookupFailed = true;
            return null;
        }
    }

    private static ItemFactory s_itemFactory;

    /// <summary>
    /// SPT 4.1 does not register <see cref="ItemFactory"/> in the mod's VContainer scope and the
    /// game keeps it on classes we cannot reach, so build one from the live templates.
    /// </summary>
    private static ItemFactory GetItemFactory()
    {
        if (s_itemFactory != null)
        {
            return s_itemFactory;
        }

        try
        {
            ObjectsFactory objectsFactory = GetObjectsFactory();
            if (objectsFactory == null)
            {
                return null;
            }

            s_itemFactory = new ItemFactory(objectsFactory._itemTemplates);
            return s_itemFactory;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ResourceKey[] GetResourceKeys(Item containerItem)
    {
        ResourceKey[] resourceKeys;
        if (containerItem is ContainerData container)
        {
            resourceKeys = container
                .GetAllItemsFromCollection()
                .AsValueEnumerable()
                .SelectMany(item => item.Template.AllResources)
                .ToArray();
        }
        else
        {
            resourceKeys = containerItem.Template.AllResources.ToArray();
        }

        return resourceKeys;
    }

    public async UniTask<Item> RequestContainerItem(CancellationToken cancellationToken = default)
    {
        try
        {
            if (configService.LoggingEnabled.Value)
            {
                logger.LogInfo("Requesting container item...");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            AirdropLootResponse lootResponse = (
                await (
                    (ClientBackendSession)ClientAppUtils.GetClientApp().GetClientBackEndSession()
                ).LoadLootContainerData(null)
                    .AsUniTask()
                    .Timeout(
                        TimeSpan.FromSeconds(10),
                        DelayType.Realtime,
                        taskCancellationTokenSource: cts
                    )
            ).Value;

            cancellationToken.ThrowIfCancellationRequested();

            if (lootResponse?.data == null)
            {
                return await UniTask.FromException<Item>(
                    new NullReferenceException("Heli crash loot response is null")
                );
            }

            ItemFactory itemFactory = GetItemFactory();
            if (itemFactory == null)
            {
                return await UniTask.FromException<Item>(
                    new NullReferenceException("Heli crash: ItemFactory is not reachable")
                );
            }

            Item containerItem = itemFactory
                .FlatItemsToTree(lootResponse.data, false, null)
                .Items[lootResponse.data[0]._id];

            if (configService.LoggingEnabled.Value)
            {
                logger.LogInfo($"Container item generated! Item={containerItem}");
            }

            return containerItem;
        }
        catch (OperationCanceledException ex)
        {
            if (configService.LoggingEnabled.Value)
            {
                logger.LogInfo($"Canceled creating container item. {ex.Message}");
            }
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(
                $"Request failed trying to create container item! {ex.Message}\n{ex.StackTrace}"
            );
            return null;
        }
    }
}
