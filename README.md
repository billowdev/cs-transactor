## `IAtomicTransactor` Interface and `AtomicTransactor` Class: Documentation and Usage Guide

This document provides detailed information on the `IAtomicTransactor` interface and its implementation, the `AtomicTransactor` class. This component helps manage database transactions within a .NET application using Entity Framework Core, focusing on guaranteeing atomicity (all-or-nothing) of database operations.

**Purpose:**

The `IAtomicTransactor` and `AtomicTransactor` aim to simplify and standardize transaction management in applications that interact with databases. It encapsulates the logic for starting, committing, and rolling back transactions, ensuring that operations across multiple repositories or data access components are treated as a single atomic unit. This prevents data inconsistencies that can occur if only some of the intended database changes are applied.

**Key Features:**

*   **Transaction Management:** Provides methods to begin, commit, and rollback database transactions.
*   **Atomicity:** Ensures that all database operations within a transaction succeed or, in case of failure, all changes are rolled back.
*   **Asynchronous Operations:** All methods are asynchronous, making the solution suitable for modern, scalable applications.
*   **Cancellation Support:** Includes `CancellationToken` parameters for cancelling long-running operations.
*   **Resource Management:** Implements `IDisposable` and `IAsyncDisposable` to properly release resources, especially database connections.
*   **Safety Checks:** Includes checks to prevent misuse, such as committing a transaction multiple times or using a disposed object.
*   **State Tracking:** Maintains internal state to track transaction status.

### IAtomicTransactor Interface

The `IAtomicTransactor` interface defines the contract for managing database transactions.

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

public interface IAtomicTransactor : IDisposable, IAsyncDisposable
{
	Task BeginTransactionAsync(CancellationToken cancellationToken = default);
	Task CommitAsync(CancellationToken cancellationToken = default);
	Task RollbackAsync(CancellationToken cancellationToken = default);
	Task SaveChangesAsync(CancellationToken cancellationToken = default);
	bool IsTransactionActive { get; }
}
```

**Members:**

*   **`BeginTransactionAsync(CancellationToken cancellationToken = default)`**:  `Task`
    *   Asynchronously starts a new database transaction.
    *   `cancellationToken`:  A `CancellationToken` to propagate notification that the operation should be canceled.
    *   Throws an `InvalidOperationException` if a transaction is already in progress.
    *   Throws an `ObjectDisposedException` if the object is disposed.

*   **`CommitAsync(CancellationToken cancellationToken = default)`**:  `Task`
    *   Asynchronously commits the current database transaction, persisting all changes to the database.
    *   `cancellationToken`:  A `CancellationToken` to propagate notification that the operation should be canceled.
    *   Throws an `InvalidOperationException` if a transaction has not been started or has already been completed.
    *   Throws an `ObjectDisposedException` if the object is disposed.

*   **`RollbackAsync(CancellationToken cancellationToken = default)`**:  `Task`
    *   Asynchronously rolls back the current database transaction, discarding all changes made since the transaction was started.
    *   `cancellationToken`: A `CancellationToken` to propagate notification that the operation should be canceled.
    *   It's safe to call even if no transaction is active, providing a safeguard.
    *   Throws an `ObjectDisposedException` if the object is disposed.

*   **`SaveChangesAsync(CancellationToken cancellationToken = default)`**:  `Task`
    *   Asynchronously saves all changes made to the `DbContext` to the underlying database.  This does NOT commit the transaction; it simply persists changes within the scope of the current transaction.  Should be called before `CommitAsync`.
    *   `cancellationToken`:  A `CancellationToken` to propagate notification that the operation should be canceled.
    *   Throws an `InvalidOperationException` if a transaction has not been started or has already been completed.
    *   Throws an `ObjectDisposedException` if the object is disposed.

*   **`IsTransactionActive`**:  `bool`
    *   A read-only property that indicates whether a transaction is currently active.
    *   Returns `true` if a transaction has been started and neither committed nor rolled back.
    *   Returns `false` otherwise.

*   **`Dispose()`**:  `void`
    *   Implements the `IDisposable` interface.  Releases unmanaged resources.  In this case, it attempts to roll back the transaction if it's still active.

*   **`DisposeAsync()`**:  `ValueTask`
    *   Implements the `IAsyncDisposable` interface.  Asynchronously releases unmanaged resources.  It's preferred over `Dispose()` in asynchronous contexts.

### AtomicTransactor Class

The `AtomicTransactor` class provides a concrete implementation of the `IAtomicTransactor` interface.

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using App.Sources.Infra.Infra.Database; // Assuming this is your ApplicationDBContext namespace
using Microsoft.EntityFrameworkCore.Storage;

public class AtomicTransactor : IAtomicTransactor
{
	private readonly ApplicationDBContext _context;
	private IDbContextTransaction? _transaction;
	private bool _disposed = false;
	private bool _completedTransaction = false; // Tracks if commit or rollback has been called

	public AtomicTransactor(ApplicationDBContext context)
	{
		_context = context ?? throw new ArgumentNullException(nameof(context));
	}

	public bool IsTransactionActive => _transaction != null && !_completedTransaction;

	public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedCheck();
		if (_transaction != null)
		{
			throw new InvalidOperationException("A transaction is already in progress.");
		}
		_transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
		_completedTransaction = false; // Reset for the new transaction
	}

	public async Task CommitAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedCheck();
		TransactionNullCheck("Cannot commit a transaction that has not been started.");
		if (_completedTransaction)
		{
			throw new InvalidOperationException("Transaction has already been completed (committed or rolled back).");
		}

		try
		{
			// Save changes to the context before committing the database transaction
			await _context.SaveChangesAsync(cancellationToken);
			if (_transaction != null)
			{
				await _transaction.CommitAsync(cancellationToken);
			}
			else
			{
				throw new InvalidOperationException("Transaction is null and cannot be committed.");
			}
			_completedTransaction = true;
		}
		catch (Exception)
		{
			// Attempt to roll back if commit fails
			await RollbackAsyncInternal(cancellationToken); // Use internal to avoid redundant checks
			throw;
		}
	}

	public async Task RollbackAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedCheck();
		// Allow rollback even if transaction wasn't explicitly started by this instance,
		// or if _completedTransaction is true, as a safeguard.
		if (_transaction != null && !_completedTransaction)
		{
			await RollbackAsyncInternal(cancellationToken);
		}
	}

	private async Task RollbackAsyncInternal(CancellationToken cancellationToken = default)
	{
		// Internal helper to be called from CommitAsync's catch and DisposeAsync
		if (_transaction != null && !_completedTransaction) // Check again as state might change
		{
			try
			{
				await _transaction.RollbackAsync(cancellationToken);
			}
			finally // Ensure it's marked as completed even if RollbackAsync itself throws (unlikely for EF Core)
			{
				_completedTransaction = true;
			}
		}
	}


	public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedCheck();
		TransactionNullCheck("Cannot save changes when a transaction has not been started or is completed.");
		if (_completedTransaction)
		{
			throw new InvalidOperationException("Cannot save changes after the transaction has been completed.");
		}
		await _context.SaveChangesAsync(cancellationToken);
	}

	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	protected virtual void Dispose(bool disposing)
	{
		if (!_disposed)
		{
			if (disposing)
			{
				// In synchronous dispose, we rely on the DbContextTransaction's own Dispose
				// to roll back if it's still active.
				// We cannot reliably call async methods here.
				if (_transaction != null && !_completedTransaction)
				{
					// EF Core's IDbContextTransaction.Dispose() will roll back
					// if the transaction was not committed.
					_transaction.Dispose();
					_completedTransaction = true;
				}
			}
			_transaction = null; // Clear the reference
			_disposed = true;
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (!_disposed)
		{
			if (_transaction != null && !_completedTransaction)
			{
				await RollbackAsyncInternal(); // Ensure rollback if not completed
			}

			if (_transaction != null)
			{
				await _transaction.DisposeAsync();
			}
			_transaction = null;
			_disposed = true;
		}
		GC.SuppressFinalize(this);
	}

	private void ObjectDisposedCheck()
	{
		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(AtomicTransactor));
		}
	}

	private void TransactionNullCheck(string message)
	{
		if (_transaction == null)
		{
			throw new InvalidOperationException(message);
		}
	}
}
```

**Constructors:**

*   **`AtomicTransactor(ApplicationDBContext context)`**:
    *   Initializes a new instance of the `AtomicTransactor` class.
    *   `context`:  An instance of the `ApplicationDBContext` that will be used to interact with the database. Throws `ArgumentNullException` if `context` is null.

**Methods:**

The `AtomicTransactor` class implements all the methods defined in the `IAtomicTransactor` interface with the behavior described above.

**Private Helper Methods:**

*   **`ObjectDisposedCheck()`**:
    *   Throws an `ObjectDisposedException` if the `AtomicTransactor` instance has already been disposed.

*   **`TransactionNullCheck(string message)`**:
    *   Throws an `InvalidOperationException` if no transaction has been started (`_transaction` is null).
    *   `message`: The message to include in the exception.

*   **`RollbackAsyncInternal(CancellationToken cancellationToken = default)`**:
    *   A helper method to encapsulate the actual rollback logic, called from both `RollbackAsync` and `DisposeAsync`.
    *   It ensures that `_completedTransaction` is set to `true` even if the rollback operation itself fails.

### Usage Example

This example demonstrates how to use the `AtomicTransactor` in a service layer to perform a transactional operation.

**1. Dependency Injection Configuration:**

Register `ApplicationDBContext` and `AtomicTransactor` with your dependency injection container (e.g., in `Startup.cs` or `Program.cs`).

```csharp
using Microsoft.Extensions.DependencyInjection;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        // Configure your DbContext
        services.AddDbContext<ApplicationDBContext>(options =>
        {
            // Configure your database provider and connection string here
            options.UseSqlServer(Configuration.GetConnectionString("DefaultConnection"));
        });

        // Register the AtomicTransactor
        services.AddScoped<IAtomicTransactor, AtomicTransactor>();

        // Register your repositories and services
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IProfileRepository, ProfileRepository>();
        services.AddScoped<IMyService, MyService>();
    }
}
```

**2. Repository Interfaces and Implementations (Example):**

```csharp
// UserRepository Interface
public interface IUserRepository
{
    Task AddAsync(User user);
    // Other user-related methods
}

// UserRepository Implementation
public class UserRepository : IUserRepository
{
    private readonly ApplicationDBContext _context;

    public UserRepository(ApplicationDBContext context)
    {
        _context = context;
    }

    public async Task AddAsync(User user)
    {
        _context.Users.Add(user);
        // NOTE: No SaveChangesAsync here!  That's handled by the AtomicTransactor.
    }

    // Other methods
}

// ProfileRepository Interface
public interface IProfileRepository
{
    Task AddAsync(UserProfile profile);
    // Other profile-related methods
}

// ProfileRepository Implementation
public class ProfileRepository : IProfileRepository
{
    private readonly ApplicationDBContext _context;

    public ProfileRepository(ApplicationDBContext context)
    {
        _context = context;
    }

    public async Task AddAsync(UserProfile profile)
    {
        _context.UserProfiles.Add(profile);
        // NOTE: No SaveChangesAsync here!  That's handled by the AtomicTransactor.
    }
    // Other methods
}

```

**3. Service Interface and Implementation:**

```csharp
// Service Interface
public interface IMyService
{
    Task CreateUserWithProfileAsync(User user, UserProfile profile, CancellationToken cancellationToken = default);
}

// Service Implementation
public class MyService : IMyService
{
    private readonly IUserRepository _userRepository;
    private readonly IProfileRepository _profileRepository;
    private readonly IAtomicTransactor _atomicTransactor;

    public MyService(IUserRepository userRepository, IProfileRepository profileRepository, IAtomicTransactor atomicTransactor)
    {
        _userRepository = userRepository;
        _profileRepository = profileRepository;
        _atomicTransactor = atomicTransactor;
    }

    public async Task CreateUserWithProfileAsync(User user, UserProfile profile, CancellationToken cancellationToken = default)
    {
        await using (var transactor = _atomicTransactor)
        {
            await transactor.BeginTransactionAsync(cancellationToken);
            try
            {
                await _userRepository.AddAsync(user);
                profile.UserId = user.id;
                await _profileRepository.AddAsync(profile);

                await transactor.SaveChangesAsync(cancellationToken);
                await transactor.CommitAsync(cancellationToken);
            }
            catch (Exception)
            {
                await transactor.RollbackAsync(cancellationToken);
                throw; // Re-throw the exception to be handled further up the call stack
            }
        } // DisposeAsync is called here, ensuring rollback if needed
    }
}
```

**4. Controller (Example):**

```csharp
using Microsoft.AspNetCore.Mvc;
using System.Threading;
using System.Threading.Tasks;

[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly IMyService _myService;

    public UsersController(IMyService myService)
    {
        _myService = myService;
    }

    [HttpPost]
    public async Task<IActionResult> CreateUser([FromBody] User user, [FromBody] UserProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            await _myService.CreateUserWithProfileAsync(user, profile, cancellationToken);
            return Ok(); // Or CreatedAtAction, etc.
        }
        catch (Exception ex)
        {
            // Log the exception
            return StatusCode(500, "An error occurred.");
        }
    }
}
```

**Explanation of the Workflow:**

1.  **Request Arrives:** An HTTP request is made to the `CreateUser` action in the `UsersController`.
2.  **Service Invoked:** The `CreateUser` action calls the `CreateUserWithProfileAsync` method of the `IMyService` (which is injected as `MyService`).
3.  **`AtomicTransactor` Used:**
    *   The `await using` statement creates an `AtomicTransactor` instance, ensuring that `DisposeAsync` is called when the block exits (whether successfully or due to an exception).
    *   `transactor.BeginTransactionAsync()`: Starts a database transaction.
    *   Repository calls:
        *   `_userRepository.AddAsync(user)`: Adds the user to the database (the repository should not call `SaveChanges`).
        *   `_profileRepository.AddAsync(profile)`: Adds the user profile to the database (again, no `SaveChanges`).
    *   `transactor.SaveChangesAsync()`:  Saves ALL changes tracked by the DbContext to the database, preparing to commit the transaction.
    *   `transactor.CommitAsync()`: Commits the transaction, making the changes permanent in the database.
    *   If any exception occurs within the `try` block:
        *   `transactor.RollbackAsync()`: Rolls back the transaction, discarding all changes.
        *   The exception is re-thrown to be handled by the controller or a global exception handler.
    *   `DisposeAsync` (called by `await using`) guarantees the resources are released and the transaction is rolled back if it hasn't been already.
4.  **Response Sent:** The controller sends an appropriate HTTP response based on the outcome of the operation.

### Key Considerations and Best Practices

*   **`SaveChangesAsync` Placement:** Ensure that `SaveChangesAsync` is called before `CommitAsync`.  `CommitAsync` only commits the changes *already* saved to the database context.
*   **Exception Handling:** Always handle exceptions within the service layer and roll back the transaction if an error occurs.
*   **Dependency Injection:** Use dependency injection to provide the `ApplicationDBContext` and `AtomicTransactor` instances to your services.
*   **Resource Management:** Use `await using` (or `using` in synchronous contexts) to ensure that the `AtomicTransactor` is disposed of properly.
*   **Single DbContext Instance:**  Make sure all the repositories that are part of the same transaction use the *same* `ApplicationDBContext` instance. Dependency Injection will handle this correctly if you register the context with a scope that matches the transaction scope (e.g., `AddScoped`).
*   **Isolation Levels:**  Consider specifying an appropriate transaction isolation level for your application.  The default isolation level (usually `ReadCommitted`) is often sufficient, but you might need a higher isolation level (e.g., `Serializable`) to prevent certain types of concurrency issues. This can be configured when calling `BeginTransactionAsync()`.

### Benefits of Using `IAtomicTransactor` and `AtomicTransactor`

*   **Simplified Transaction Management:** Centralizes transaction management logic.
*   **Improved Data Consistency:** Ensures atomicity of database operations.
*   **Reduced Boilerplate Code:** Reduces the amount of repetitive transaction management code in your application.
*   **Increased Testability:** Facilitates unit testing by allowing you to mock the `IAtomicTransactor` interface.
*   **Enhanced Maintainability:** Makes it easier to maintain and update transaction management logic in the future.

By implementing the `IAtomicTransactor` interface and utilizing the `AtomicTransactor` class, you can create more robust, reliable, and maintainable .NET applications that interact with databases. Remember to adapt the code and configuration to suit your specific application requirements.
